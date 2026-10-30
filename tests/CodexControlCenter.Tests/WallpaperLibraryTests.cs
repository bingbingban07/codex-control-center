using System.Security.Cryptography;
using CodexControlCenter.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

public static class WallpaperLibraryTests
{
    public static async Task RunAsync()
    {
        using Fixture fixture = Fixture.Create();
        WallpaperLibrary library = fixture.Library;
        string source = fixture.CreateBmp("source.bmp", 2, 2);
        byte[] sourceBytes = File.ReadAllBytes(source);

        WallpaperAsset first = await library.ImportAsync(source);
        Require(first.Id == Hash(sourceBytes), "Imported asset id is not the content hash.");
        Require(first.Name == "source.bmp" && first.Width == 2 && first.Height == 2, "Imported metadata is wrong.");
        Require(File.Exists(source) && File.ReadAllBytes(source).SequenceEqual(sourceBytes), "Import changed or removed the source.");
        Require(first.Path.StartsWith(Path.Combine(library.Root, "library") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "Imported image is outside the managed library.");

        string duplicate = fixture.CreateBmp("duplicate.bmp", 2, 2);
        WallpaperAsset second = await library.ImportAsync(duplicate);
        Require(second.Id == first.Id && second.Path == first.Path, "Identical content was not deduplicated.");
        Require(library.List().Count == 1, "Duplicate import created another managed entry.");

        WallpaperAsset png = await library.ImportAsync(fixture.WriteFile("valid.png", embeddedPng));
        Require(png.Width == 1 && png.Height == 1, "A PNG image did not decode with WIC.");
        Require(library.GetDataUri(png.Id).StartsWith("data:image/png;base64,", StringComparison.Ordinal),
            "PNG data URI media type is wrong.");
        WallpaperAsset jpeg = await library.ImportAsync(
            await fixture.CreateEncodedImageAsync("valid.jpg", BitmapEncoder.JpegEncoderId));
        Require(library.GetDataUri(jpeg.Id).StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal),
            "JPEG was not decoded and returned as a JPEG data URI.");
        WallpaperAsset webp = await library.ImportAsync(fixture.WriteFile("valid.webp", embeddedWebp));
        Require(library.GetDataUri(webp.Id).StartsWith("data:image/webp;base64,", StringComparison.Ordinal),
            "WEBP was not decoded and returned as a WEBP data URI.");

        string dataUri = library.GetDataUri(first.Id);
        Require(dataUri.StartsWith("data:image/bmp;base64,", StringComparison.Ordinal), "Data URI media type is wrong.");
        Require(Convert.FromBase64String(dataUri[(dataUri.IndexOf(',') + 1)..]).SequenceEqual(sourceBytes),
            "Data URI does not contain the managed source bytes.");
        RequireThrows<ArgumentException>(() => library.GetDataUri("../../outside"),
            "Traversal image id was accepted.");
        RequireThrows<ArgumentException>(() => library.Delete("..\\outside"),
            "Traversal delete id was accepted.");

        var initialSnapshot = library.LoadSnapshot();
        Require(initialSnapshot.Settings == new WallpaperSettings() && initialSnapshot.Revision == "missing",
            "Missing settings did not return defaults and the missing revision.");
        string missingRevision = initialSnapshot.Revision;
        library.SaveSettings(new WallpaperSettings { Enabled = true, ImageId = first.Id }, missingRevision);
        var firstSnapshot = library.LoadSnapshot();
        string firstRevision = firstSnapshot.Revision;
        Require(firstSnapshot.Settings.Enabled && firstSnapshot.Settings.ImageId == first.Id,
            "Wallpaper settings did not round-trip.");
        RequireThrows<InvalidOperationException>(() => library.Delete(first.Id),
            "The active wallpaper was deleted.");
        Require(library.List().Any(asset => asset.Id == first.Id), "Rejected deletion removed the active image.");
        RequireThrows<ArgumentException>(() => WallpaperLibrary.Validate(
            new WallpaperSettings { Enabled = true }), "Enabled settings without an image were accepted.");
        foreach (string fit in new[] { "fill", "stretch", "none", "scale-down", "auto" })
        {
            RequireThrows<ArgumentOutOfRangeException>(() =>
                    WallpaperLibrary.Validate(new WallpaperSettings { Fit = fit }),
                $"Unsupported fit value '{fit}' was accepted.");
        }
        WallpaperLibrary.Validate(new WallpaperSettings { Fit = "cover", Blur = 64 });
        Require(Math.Abs(new WallpaperSettings{Opacity=.7}.Dimming-.3)<1e-9,"Legacy intensity was not converted to dimming.");
        Require(new WallpaperSettings{Opacity=.2,Darkness=.6}.Dimming==.6,"Explicit dimming did not override legacy opacity.");
        foreach(double darkness in new[]{double.NaN,double.PositiveInfinity,-.01,1.01})
            RequireThrows<ArgumentOutOfRangeException>(()=>WallpaperLibrary.Validate(new WallpaperSettings{Darkness=darkness}),"Invalid dimming was accepted.");
        var dimLibrary=new WallpaperLibrary(Path.Combine(fixture.Root,"dim-settings"));
        Directory.CreateDirectory(dimLibrary.Root);
        File.WriteAllText(dimLibrary.SettingsPath,"{\"opacity\":0.7}");
        var legacy=dimLibrary.LoadSnapshot();
        Require(legacy.Settings.Darkness is null&&Math.Abs(legacy.Settings.Dimming-.3)<1e-9,"Legacy saved settings were not read compatibly.");
        var dimmed=dimLibrary.SaveSnapshot(legacy.Settings with{Opacity=1,Darkness=.45},legacy.Revision);
        Require(dimLibrary.LoadSettings().Darkness==.45&&dimmed.Settings.Opacity==1,"Dimming did not round-trip.");
        RequireThrows<ArgumentOutOfRangeException>(() =>
            WallpaperLibrary.Validate(new WallpaperSettings { Blur = 64.01 }), "Blur above 64 px was accepted.");
        RequireThrows<ArgumentOutOfRangeException>(() =>
            WallpaperLibrary.Validate(new WallpaperSettings { Blur = 1e308 }), "An excessive finite blur was accepted.");

        library.SaveSettings(library.LoadSettings() with { Fit = "contain", Opacity = .5 }, firstRevision);
        string secondRevision = library.SettingsRevision();
        Require(File.Exists(library.SettingsPath + ".bak"), "Saving settings did not create a backup.");
        string backup = File.ReadAllText(library.SettingsPath + ".bak");
        Require(backup.Contains("\"opacity\": 0.8", StringComparison.Ordinal),
            "Settings backup does not contain the original content.");

        WallpaperLibrary competing = new(library.Root);
        string staleRevision = competing.SettingsRevision();
        library.SaveSettings(library.LoadSettings() with { Blur = 4 }, secondRevision);
        string conflictedBefore = File.ReadAllText(library.SettingsPath);
        RequireThrowsMessage<InvalidOperationException>(() =>
                competing.SaveSettings(library.LoadSettings() with { Blur = 8 }, staleRevision),
            "设置已被其他操作更新",
            "A stale revision overwrote newer settings.");
        Require(File.ReadAllText(library.SettingsPath) == conflictedBefore, "Revision conflict changed settings.");
        RequireThrows<InvalidOperationException>(() => library.SaveSettings(
            library.LoadSettings() with { ImageId = new string('a', 64) }, library.SettingsRevision()),
            "Settings accepted a missing image id.");

        string damagedBefore = File.ReadAllText(library.SettingsPath);
        string backupBefore = File.ReadAllText(library.SettingsPath + ".bak");
        File.WriteAllText(library.SettingsPath, "{ damaged");
        RequireThrows<InvalidDataException>(() => library.LoadSettings(), "Damaged settings were silently accepted.");
        string damagedRevision = library.SettingsRevision();
        RequireThrows<InvalidDataException>(() => library.SaveSettings(new WallpaperSettings(), damagedRevision),
            "Damaged settings were overwritten.");
        Require(File.ReadAllText(library.SettingsPath) == "{ damaged", "Damaged settings were modified.");
        Require(File.ReadAllText(library.SettingsPath + ".bak") == backupBefore, "Damaged save changed the valid backup.");
        var recovered=library.RecoverSettings(true,damagedRevision);
        Require(File.ReadAllText(library.SettingsPath+".bak")==backupBefore,"Recovery overwrote the valid backup.");
        Require(Directory.EnumerateFiles(library.Root,"*.recovered-*.json").Any(p=>File.ReadAllText(p)=="{ damaged"),"Recovery did not preserve damaged source bytes.");
        Require(recovered.Revision==library.LoadSnapshot().Revision,"Recovery revision is inconsistent.");
        var restoredSnapshot = library.LoadSnapshot();
        WallpaperSettings restored = restoredSnapshot.Settings;
        Require(restored.Fit == "contain" && restored.Opacity == .5 && restored.Blur == 0,
            "Backup content did not restore the prior valid settings.");
        Require(damagedBefore != File.ReadAllText(library.SettingsPath), "Backup recovery fixture was not exercised.");

        WallpaperAsset disposable = await library.ImportAsync(fixture.CreateBmp("disposable.bmp", 3, 2));
        library.SaveSettings(library.LoadSettings() with { Enabled = false, ImageId = disposable.Id },
            library.SettingsRevision());
        var beforeDisposableDelete = library.LoadSnapshot();
        var afterDisposableDelete = library.Delete(disposable.Id, beforeDisposableDelete.Revision);
        Require(afterDisposableDelete.Settings.ImageId is null,
            "Deleting a disabled reference left a dangling image id.");
        Require(afterDisposableDelete.Revision == library.LoadSnapshot().Revision,
            "The delete overload returned a stale settings revision.");
        Require(library.List().All(asset => asset.Id != disposable.Id), "Deleting a disabled reference left the image.");

        WallpaperAsset rollbackImage=await library.ImportAsync(fixture.CreateBmp("delete-rollback.bmp",5,3));
        var rollbackSaved=library.SaveSnapshot(new WallpaperSettings{Enabled=false,ImageId=rollbackImage.Id},library.SettingsRevision());
        string rollbackBefore=File.ReadAllText(library.SettingsPath);
        string backupPath=library.SettingsPath+".bak";File.Delete(backupPath);Directory.CreateDirectory(backupPath);
        bool deleteFailed=false;try{library.Delete(rollbackImage.Id,rollbackSaved.Revision);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){deleteFailed=true;}
        Require(deleteFailed&&File.Exists(rollbackImage.Path)&&File.ReadAllText(library.SettingsPath)==rollbackBefore,"Failed delete did not restore image and settings.");
        Directory.Delete(backupPath);
        string moved=Path.GetDirectoryName(rollbackImage.Path)!+"-missing-test";Directory.Move(Path.GetDirectoryName(rollbackImage.Path)!,moved);
        RequireThrows<FileNotFoundException>(()=>library.Delete(rollbackImage.Id,rollbackSaved.Revision),"Missing referenced asset deletion was accepted.");
        Require(File.ReadAllText(library.SettingsPath)==rollbackBefore,"Missing asset deletion unexpectedly modified settings.");
        Directory.Move(moved,Path.GetDirectoryName(rollbackImage.Path)!);
        library.Delete(rollbackImage.Id,rollbackSaved.Revision);

        WallpaperAsset staleDeleteImage = await library.ImportAsync(fixture.CreateBmp("stale-delete.bmp", 4, 2));
        var referencedSnapshot = library.LoadSnapshot();
        library.SaveSettings(referencedSnapshot.Settings with { Enabled = false, ImageId = staleDeleteImage.Id },
            referencedSnapshot.Revision);
        string staleDeleteRevision = library.SettingsRevision();
        var newerSnapshot = library.LoadSnapshot();
        library.SaveSettings(newerSnapshot.Settings with { Blur = 37 }, newerSnapshot.Revision);
        RequireThrowsMessage<InvalidOperationException>(() =>
                library.Delete(staleDeleteImage.Id, staleDeleteRevision),
            "设置已被其他操作更新",
            "A stale delete revision removed an image.");
        Require(library.List().Any(asset => asset.Id == staleDeleteImage.Id),
            "A stale delete revision changed the image library.");
        library.Delete(staleDeleteImage.Id);
        Require(library.List().All(asset => asset.Id != staleDeleteImage.Id),
            "Deleting through the void overload did not remove the image.");

        string invalidExtension = fixture.WriteFile("fake.jpg", embeddedPng);
        await RequireThrowsAsync<InvalidDataException>(() => library.ImportAsync(invalidExtension),
            "PNG bytes with a JPEG extension were accepted.");
        string corrupted = fixture.CreateBmp("corrupted.bmp", 2, 2);
        using (var stream = new FileStream(corrupted, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(30);
        }
        await RequireThrowsAsync<InvalidDataException>(() => library.ImportAsync(corrupted),
            "A truncated image was accepted.");
        string svg = fixture.WriteFile("vector.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray());
        await RequireThrowsAsync<InvalidDataException>(() => library.ImportAsync(svg),
            "SVG was accepted.");
        string oversizedDimension = fixture.CreateBmpHeaderOnly("too-wide.bmp", 16_385, 1);
        await RequireThrowsAsync<InvalidDataException>(() => library.ImportAsync(oversizedDimension),
            "An image wider than 16384 pixels was accepted.");
        string oversizedPixels = fixture.CreateBmpHeaderOnly("too-many-pixels.bmp", 8_193, 8_192);
        await RequireThrowsAsync<InvalidDataException>(() => library.ImportAsync(oversizedPixels),
            "An image over 64 Mi pixels was accepted.");
        string oversizedFile = fixture.WriteFile("too-big.bmp", new byte[24 * 1024 * 1024 + 1]);
        await RequireThrowsAsync<InvalidDataException>(() => library.ImportAsync(oversizedFile),
            "A file larger than 24 MiB was accepted.");

        string corruptedManagedFile = first.Path;
        File.WriteAllText(corruptedManagedFile, "broken");
        IReadOnlyList<WallpaperAsset> afterDamage = library.List();
        Require(afterDamage.All(asset => asset.Id != first.Id), "A corrupted library entry was returned by List.");
        RequireThrows<FileNotFoundException>(() => library.GetDataUri(first.Id),
            "GetDataUri returned a corrupted managed image.");

        TestReparsePoint(fixture);
    }

    private static void TestReparsePoint(Fixture fixture)
    {
        string root = Path.Combine(fixture.Root, "linked-state");
        Directory.CreateDirectory(root);
        string outside = Path.Combine(fixture.Root, "outside-library");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "library"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var linked = new WallpaperLibrary(root);
        string source = fixture.CreateBmp("linked-source.bmp", 1, 1);
        RequireThrows<IOException>(() => linked.ImportAsync(source).GetAwaiter().GetResult(),
            "A library reparse point was followed.");
        Require(!Directory.EnumerateFileSystemEntries(outside).Any(), "Import wrote through a library reparse point.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void RequireThrowsMessage<T>(Action action, string messageFragment, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex) when (ex.Message.Contains(messageFragment, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static readonly byte[] embeddedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] embeddedWebp = Convert.FromBase64String(
        "UklGRiwAAABXRUJQVlA4TB8AAAAvAUAAAB8gEEjeHzqN+RcQFPwf3fxHZA/gBgwR/Q8BAA==");

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root)
        {
            Root = root;
            Library = new WallpaperLibrary(Path.Combine(root, "state"));
        }

        public string Root { get; }
        public WallpaperLibrary Library { get; }

        public static Fixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexControlCenter.WallpaperLibraryTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root);
        }

        public string CreateBmp(string name, int width, int height)
        {
            int stride = checked(((width * 3) + 3) & ~3);
            int pixelBytes = checked(stride * height);
            byte[] bytes = new byte[54 + pixelBytes];
            WriteBmpHeaders(bytes, width, height, stride, pixelBytes);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = 54 + (y * stride) + (x * 3);
                    bytes[offset] = (byte)(32 + x * 17);
                    bytes[offset + 1] = (byte)(64 + y * 23);
                    bytes[offset + 2] = 160;
                }
            }

            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string CreateBmpHeaderOnly(string name, int width, int height)
        {
            byte[] bytes = new byte[54];
            WriteBmpHeaders(bytes, width, height, ((width * 3) + 3) & ~3, 0);
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string WriteFile(string name, byte[] bytes)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public async Task<string> CreateEncodedImageAsync(string name, Guid encoderId)
        {
            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, output).AsTask();
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                2,
                2,
                96,
                96,
                new byte[]
                {
                    0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255
                });
            await encoder.FlushAsync().AsTask();

            output.Seek(0);
            uint size = checked((uint)output.Size);
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync(size).AsTask();
            byte[] bytes = new byte[checked((int)size)];
            reader.ReadBytes(bytes);
            return WriteFile(name, bytes);
        }

        private static void WriteBmpHeaders(byte[] bytes, int width, int height, int stride, int pixelBytes)
        {
            Span<byte> data = bytes;
            data[0] = (byte)'B';
            data[1] = (byte)'M';
            WriteInt32(data, 2, checked(54 + pixelBytes));
            WriteInt32(data, 10, 54);
            WriteInt32(data, 14, 40);
            WriteInt32(data, 18, width);
            WriteInt32(data, 22, height);
            WriteInt16(data, 26, 1);
            WriteInt16(data, 28, 24);
            WriteInt32(data, 34, checked(stride * Math.Max(height, 1)));
            WriteInt32(data, 38, 2_835);
            WriteInt32(data, 42, 2_835);
        }

        private static void WriteInt16(Span<byte> data, int offset, short value) =>
            BitConverter.TryWriteBytes(data[offset..], value);

        private static void WriteInt32(Span<byte> data, int offset, int value) =>
            BitConverter.TryWriteBytes(data[offset..], value);

        public void Dispose()
        {
            string basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "CodexControlCenter.WallpaperLibraryTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refused to clean an unexpected test path.");
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
