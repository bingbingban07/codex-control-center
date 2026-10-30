using System.Diagnostics;
using System.Text;
using CodexControlCenter.Core;

public static class ImageGenerationRunnerTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexControlCenter.ImageGenerationRunnerTests", "生图 测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = await CreateFakeScriptAsync(root);
            var runner = new ImageGenerationRunner(FindPythonExecutable(), script);

            await AssertSuccessAsync(runner, root, "success-png 输出", ImageGenerationFormat.Png, "image.png");
            await AssertSuccessAsync(runner, root, "success-jpeg 输出", ImageGenerationFormat.Jpeg, "image.jpeg");
            await AssertSuccessAsync(runner, root, "success-webp 输出", ImageGenerationFormat.Webp, "image.webp");
            await AssertFailureAsync(runner, root, "error 输出", "error", "SECRET_KEY", "SECRET_BEARER");
            await AssertLongOutputAsync(runner, root, "long 输出");
            await AssertInvalidOutputAsync(runner, root, "invalid 输出");
            await AssertTimeoutAsync(runner, root, "timeout 输出");
            await AssertCancelAsync(runner, root, "cancel 输出");
        }
        finally
        {
            try
            {
                string allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"CodexControlCenter.ImageGenerationRunnerTests"))+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(root).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected image-test cleanup path");
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task AssertSuccessAsync(
        ImageGenerationRunner runner,
        string root,
        string outputName,
        ImageGenerationFormat format,
        string expectedFileName)
    {
        var output = Path.Combine(root, outputName);
        var result = await runner.RunAsync(
            new ImageGenerationRequest(
                Path.Combine(root, "代码 home"),
                "success",
                output,
                format,
                TimeSpan.FromSeconds(5)));

        Assert(result.Status == ImageGenerationStatus.Succeeded, "success status for " + format);
        Assert(result.OutputPath is not null && Path.GetFileName(result.OutputPath) == expectedFileName, "format-specific output filename");
        Assert(File.Exists(result.OutputPath!), "success output exists");
        Assert(result.OutputBytes == new FileInfo(result.OutputPath!).Length && result.OutputBytes > 0, "reported output size");
        Assert(Directory.GetFiles(output, "prompt-*.txt").Length == 0, "prompt temp file is cleaned");
    }

    private static async Task AssertFailureAsync(
        ImageGenerationRunner runner,
        string root,
        string outputName,
        string prompt,
        params string[] secretMarkers)
    {
        var output = Path.Combine(root, outputName);
        var result = await runner.RunAsync(
            Path.Combine(root, "代码 home"),
            prompt,
            output,
            "png",
            TimeSpan.FromSeconds(5));

        Assert(result.Status == ImageGenerationStatus.Failed && result.ExitCode == 2, "error status");
        Assert(result.OutputPath is null && !File.Exists(Path.Combine(output, "image.png")), "failed output is not exposed");
        foreach (var marker in secretMarkers)
        {
            Assert(!result.Message.Contains(marker, StringComparison.OrdinalIgnoreCase), "message redacts " + marker);
            Assert(!result.StandardOutput.Contains(marker, StringComparison.OrdinalIgnoreCase), "stdout redacts " + marker);
            Assert(!result.StandardError.Contains(marker, StringComparison.OrdinalIgnoreCase), "stderr redacts " + marker);
        }

        Assert(result.StandardError.Contains("[REDACTED]", StringComparison.Ordinal), "redaction marker is visible");
    }

    private static async Task AssertLongOutputAsync(ImageGenerationRunner runner, string root, string outputName)
    {
        var output = Path.Combine(root, outputName);
        var result = await runner.RunAsync(
            Path.Combine(root, "代码 home"),
            "long",
            output,
            "png",
            TimeSpan.FromSeconds(5));

        Assert(result.Status == ImageGenerationStatus.Failed, "long output is treated as failure without image");
        Assert(result.StandardOutput.Length + result.StandardError.Length <= 64 * 1024, "diagnostics are bounded");
        Assert(result.StandardOutput.Contains("[diagnostic truncated]") || result.StandardError.Contains("[diagnostic truncated]"), "truncation marker is visible");
    }

    private static async Task AssertInvalidOutputAsync(ImageGenerationRunner runner, string root, string outputName)
    {
        var output = Path.Combine(root, outputName);
        var result = await runner.RunAsync(
            Path.Combine(root, "代码 home"),
            "invalid",
            output,
            "png",
            TimeSpan.FromSeconds(5));

        Assert(result.Status == ImageGenerationStatus.Failed && result.OutputPath is null, "invalid signature is rejected");
        Assert(!File.Exists(Path.Combine(output, "image.png")), "invalid output is deleted");
    }

    private static async Task AssertTimeoutAsync(ImageGenerationRunner runner, string root, string outputName)
    {
        var output = Path.Combine(root, outputName);
        var started = Environment.TickCount64;
        var result = await runner.RunAsync(
            Path.Combine(root, "代码 home"),
            "timeout",
            output,
            "png",
            TimeSpan.FromMilliseconds(250));

        Assert(result.Status == ImageGenerationStatus.TimedOut, "timeout status");
        Assert(Environment.TickCount64 - started < 5_000, "timeout returns promptly");
        Assert(Directory.GetFiles(output, "prompt-*.txt").Length == 0, "timeout prompt temp file is cleaned");
    }

    private static async Task AssertCancelAsync(ImageGenerationRunner runner, string root, string outputName)
    {
        var output = Path.Combine(root, outputName);
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = Environment.TickCount64;
        var result = await runner.RunAsync(
            Path.Combine(root, "代码 home"),
            "cancel",
            output,
            "png",
            TimeSpan.FromSeconds(10),
            source.Token);

        Assert(result.Status == ImageGenerationStatus.Cancelled, "cancel status");
        Assert(Environment.TickCount64 - started < 5_000, "cancel returns promptly");
    }

    private static async Task<string> CreateFakeScriptAsync(string root)
    {
        var scriptDirectory = Path.Combine(root, "fake Python scripts 目录");
        Directory.CreateDirectory(scriptDirectory);
        var script = Path.Combine(scriptDirectory, "fake_image_runner.py");
        var source = """
import argparse
import base64
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--codex-home")
parser.add_argument("--prompt-file")
parser.add_argument("--out")
parser.add_argument("--format")
parser.add_argument("--timeout")
args = parser.parse_args()
mode = Path(args.prompt_file).read_text(encoding="utf-8").strip()
out = Path(args.out)

if mode == "success":
    if args.format == "png":
        data = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    elif args.format == "jpeg":
        data = b"\xff\xd8\xff\xe0fake-jpeg"
    else:
        data = b"RIFF\x20\x00\x00\x00WEBPfake-webp"
    out.write_bytes(data)
    print("fake success " + str(out))
elif mode == "error":
    print("api_key=SECRET_KEY Bearer SECRET_BEARER", file=sys.stderr)
    raise SystemExit(2)
elif mode == "long":
    payload = "x" * 200000
    for _ in range(8):
        sys.stdout.write(payload)
        sys.stderr.write(payload)
    raise SystemExit(0)
elif mode == "invalid":
    out.write_text("not an image", encoding="utf-8")
    raise SystemExit(0)
elif mode == "timeout" or mode == "cancel":
    time.sleep(30)
else:
    print("unknown mode", file=sys.stderr)
    raise SystemExit(3)
""";
        source = "import sys\n" + source;
        await File.WriteAllTextAsync(script, source, new UTF8Encoding(false));
        return script;
    }

    private static string FindPythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "python.exe");
                if (File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
            }
        }

        throw new InvalidOperationException("python.exe was not found for ImageGenerationRunnerTests.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

