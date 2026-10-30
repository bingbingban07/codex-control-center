using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexControlCenter.Core;

public enum ImageGenerationFormat
{
    Png,
    Jpeg,
    Webp
}

public enum ImageGenerationStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}

public sealed record ImageGenerationRequest(
    string CodexHome,
    string Prompt,
    string OutputDirectory,
    ImageGenerationFormat Format,
    TimeSpan Timeout);

public sealed record ImageGenerationResult(
    ImageGenerationStatus Status,
    string? OutputPath,
    long OutputBytes,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string Message)
{
    public bool Success => Status == ImageGenerationStatus.Succeeded;
}

public sealed class ImageGenerationRunner
{
    private const int MaxDiagnosticChars = 64 * 1024;
    private const int PerStreamDiagnosticChars = MaxDiagnosticChars / 2;
    private const int ReadBufferChars = 4 * 1024;
    private const string TruncationMarker = "\n[diagnostic truncated]";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly Regex BearerRegex = new(@"(?i)\bBearer\s+[^\s""']+", RegexOptions.Compiled);
    private static readonly Regex KeyValueSecretRegex = new(
        @"(?i)\b(api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token|secret|password|authorization)\s*([=:])\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled);
    private static readonly Regex OpenAiKeyRegex = new(@"(?i)\bsk-[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled);

    private readonly string _pythonExecutable;
    private readonly string _dispatcherScript;

    public ImageGenerationRunner(string pythonExecutable, string dispatcherScript)
    {
        if (string.IsNullOrWhiteSpace(pythonExecutable))
        {
            throw new ArgumentException("Python executable is required.", nameof(pythonExecutable));
        }

        if (string.IsNullOrWhiteSpace(dispatcherScript))
        {
            throw new ArgumentException("Dispatcher script is required.", nameof(dispatcherScript));
        }

        _pythonExecutable = Path.GetFullPath(pythonExecutable);
        _dispatcherScript = Path.GetFullPath(dispatcherScript);
    }

    public Task<ImageGenerationResult> RunAsync(
        string codexHome,
        string prompt,
        string outputDirectory,
        ImageGenerationFormat format,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RunAsync(new ImageGenerationRequest(codexHome, prompt, outputDirectory, format, timeout), cancellationToken);

    public Task<ImageGenerationResult> RunAsync(
        string codexHome,
        string prompt,
        string outputDirectory,
        string format,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RunAsync(codexHome, prompt, outputDirectory, ParseFormat(format), timeout, cancellationToken);

    public async Task<ImageGenerationResult> RunAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CodexHome))
        {
            return Failed("Codex home is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            return Failed("Output directory is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Failed("Prompt is required.");
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(30))
        {
            return Failed("Timeout must be greater than zero and no more than 30 minutes.");
        }

        if (!File.Exists(_pythonExecutable))
        {
            return Failed("Python executable does not exist: " + SafeDiagnostic(_pythonExecutable));
        }

        if (!File.Exists(_dispatcherScript))
        {
            return Failed("Dispatcher script does not exist: " + SafeDiagnostic(_dispatcherScript));
        }

        string outputDirectory;
        string outputPath;
        string promptPath;
        try
        {
            outputDirectory = Path.GetFullPath(request.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);
            outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "image." + ExtensionFor(request.Format)));
            promptPath = Path.GetFullPath(Path.Combine(outputDirectory, "prompt-" + Guid.NewGuid().ToString("N") + ".txt"));
            if (File.Exists(outputPath))
            {
                return Failed("Output file already exists; refusing to overwrite it: " + SafeDiagnostic(outputPath));
            }
        }
        catch (Exception ex)
        {
            return Failed("Could not prepare output directory: " + SafeDiagnostic(ex.Message));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("", "");
        }

        try
        {
            await File.WriteAllTextAsync(promptPath, request.Prompt, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(promptPath);
            return Cancelled("", "");
        }
        catch (Exception ex)
        {
            TryDelete(promptPath);
            return Failed("Could not write prompt file: " + SafeDiagnostic(ex.Message));
        }

        var startInfo = new ProcessStartInfo(_pythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = outputDirectory
        };
        startInfo.ArgumentList.Add(_dispatcherScript);
        startInfo.ArgumentList.Add("--codex-home");
        startInfo.ArgumentList.Add(Path.GetFullPath(request.CodexHome));
        startInfo.ArgumentList.Add("--prompt-file");
        startInfo.ArgumentList.Add(promptPath);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(FormatName(request.Format));
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(Math.Clamp((int)Math.Ceiling(request.Timeout.TotalSeconds), 1, 1800).ToString());

        Process? process = null;
        Task<CapturedOutput> stdoutTask = Task.FromResult(new CapturedOutput(""));
        Task<CapturedOutput> stderrTask = Task.FromResult(new CapturedOutput(""));
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                TryDelete(promptPath);
                return Failed("Could not start the image dispatcher.");
            }

            stdoutTask = ReadBoundedAsync(process.StandardOutput, PerStreamDiagnosticChars);
            stderrTask = ReadBoundedAsync(process.StandardError, PerStreamDiagnosticChars);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(request.Timeout);
            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled;
                TryKillProcessTree(process);
                await WaitForExitWithoutCancellationAsync(process).ConfigureAwait(false);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var stdout = SafeDiagnostic(stdoutTask.Result.Text);
            var stderr = SafeDiagnostic(stderrTask.Result.Text);

            if (cancelled)
            {
                TryDelete(outputPath);
                return Cancelled(stdout, stderr);
            }

            if (timedOut)
            {
                TryDelete(outputPath);
                return TimedOut(stdout, stderr);
            }

            if (process.ExitCode != 0)
            {
                TryDelete(outputPath);
                return Failed(
                    "Image dispatcher exited with code " + process.ExitCode + "." + DiagnosticSuffix(stderr),
                    process.ExitCode,
                    stdout,
                    stderr);
            }

            if (!File.Exists(outputPath))
            {
                return Failed("The dispatcher exited successfully but did not produce the requested output.", process.ExitCode, stdout, stderr);
            }

            var fileInfo = new FileInfo(outputPath);
            if (fileInfo.Length <= 0)
            {
                TryDelete(outputPath);
                return Failed("The dispatcher produced an empty output file.", process.ExitCode, stdout, stderr);
            }

            if (!HasExpectedSignature(outputPath, request.Format))
            {
                TryDelete(outputPath);
                return Failed("The dispatcher output did not match the requested image signature.", process.ExitCode, stdout, stderr);
            }

            return new ImageGenerationResult(
                ImageGenerationStatus.Succeeded,
                outputPath,
                fileInfo.Length,
                process.ExitCode,
                stdout,
                stderr,
                "Image generated and validated.");
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            return Failed("Image generation failed: " + SafeDiagnostic(ex.Message));
        }
        finally
        {
            TryDelete(promptPath);
            process?.Dispose();
        }
    }

    public static ImageGenerationFormat ParseFormat(string format)
    {
        return format?.Trim().ToLowerInvariant() switch
        {
            "png" => ImageGenerationFormat.Png,
            "jpeg" or "jpg" => ImageGenerationFormat.Jpeg,
            "webp" => ImageGenerationFormat.Webp,
            _ => throw new ArgumentException("Format must be png, jpeg, or webp.", nameof(format))
        };
    }

    private static ImageGenerationResult Failed(
        string message,
        int? exitCode = null,
        string stdout = "",
        string stderr = "")
        => new(ImageGenerationStatus.Failed, null, 0, exitCode, stdout, stderr, SafeDiagnostic(message));

    private static ImageGenerationResult Cancelled(string stdout, string stderr)
        => new(ImageGenerationStatus.Cancelled, null, 0, null, stdout, stderr, "Image generation was cancelled.");

    private static ImageGenerationResult TimedOut(string stdout, string stderr)
        => new(ImageGenerationStatus.TimedOut, null, 0, null, stdout, stderr, "Image generation timed out; the dispatcher process tree was stopped.");

    private static string FormatName(ImageGenerationFormat format)
        => format switch
        {
            ImageGenerationFormat.Png => "png",
            ImageGenerationFormat.Jpeg => "jpeg",
            ImageGenerationFormat.Webp => "webp",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    private static string ExtensionFor(ImageGenerationFormat format)
        => format switch
        {
            ImageGenerationFormat.Png => "png",
            ImageGenerationFormat.Jpeg => "jpeg",
            ImageGenerationFormat.Webp => "webp",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    private static async Task<CapturedOutput> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var builder = new StringBuilder();
        var buffer = new char[ReadBufferChars];
        var truncated = false;
        var contentMax = Math.Max(0, maxChars - TruncationMarker.Length);
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var remaining = contentMax - builder.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    continue;
                }

                var copy = Math.Min(remaining, read);
                builder.Append(buffer, 0, copy);
                if (copy < read)
                {
                    truncated = true;
                }
            }
        }
        catch (Exception ex)
        {
            builder.Append("\n[output read failed: ").Append(SafeDiagnostic(ex.Message)).Append(']');
        }

        if (truncated)
        {
            builder.Append(TruncationMarker);
        }

        return new CapturedOutput(builder.ToString());
    }

    private static async Task WaitForExitWithoutCancellationAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool HasExpectedSignature(string path, ImageGenerationFormat format)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[12];
            var read = stream.Read(header, 0, header.Length);
            return format switch
            {
                ImageGenerationFormat.Png => read >= PngSignature.Length && header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
                ImageGenerationFormat.Jpeg => read >= JpegSignature.Length && header.AsSpan(0, JpegSignature.Length).SequenceEqual(JpegSignature),
                ImageGenerationFormat.Webp => read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
                _ => false
            };
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string DiagnosticSuffix(string stderr)
        => string.IsNullOrWhiteSpace(stderr) ? "" : " " + stderr;

    private static string SafeDiagnostic(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var safe = UserFeedback.Redact(value);
        safe = BearerRegex.Replace(safe, "Bearer [REDACTED]");
        safe = KeyValueSecretRegex.Replace(safe, "$1$2[REDACTED]");
        safe = OpenAiKeyRegex.Replace(safe, "[REDACTED]");
        return safe;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CapturedOutput(string Text);
}


