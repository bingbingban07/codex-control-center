using System.Security.Cryptography;
using CodexControlCenter.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

/// <summary>
/// Covers the productised picture library: legacy metadata, thumbnails, favourites, renaming,
/// cancellation and the zoom factor. The assertions describe observable behaviour only, the
/// thumbnail checks decode the produced files with the real Windows imaging stack.
/// </summary>
public static class WallpaperCatalogTests
{
    public static async Task RunAsync()
    {
        using Fixture fixture = Fixture.Create();
        WallpaperLibrary library = fixture.Library;

        await TestImportKeepsOriginalAsync(fixture, library);
        await TestThumbnailAsync(fixture, library);
        await TestExifOrientationAsync(fixture,library);
        await TestLegacyMetadataAsync(fixture, library);
        await TestRenameAndFavoriteAsync(fixture, library);
        await TestCancellationAsync(fixture, library);
        TestZoom(library);
        TestDamagedEntryDoesNotBreakTheList(fixture, library);
    }

    private static async Task TestImportKeepsOriginalAsync(Fixture fixture, WallpaperLibrary library)
    {
        string source = fixture.CreateBmp("keep-original.bmp", 8, 6);
        byte[] sourceBytes = File.ReadAllBytes(source);

        WallpaperAsset imported = await library.ImportAsync(source);
        Require(imported.Id == Hash(sourceBytes), "The imported id is not the content hash of the original.");
        Require(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "The import changed the original file.");
        Require(File.ReadAllBytes(imported.Path).SequenceEqual(sourceBytes),
            "The managed copy is not byte identical to the original.");
        Require(imported.ByteLength == sourceBytes.LongLength, "The reported size does not match the original.");
        Require(imported.ImportedAt > DateTimeOffset.UtcNow.AddMinutes(-10), "The import time was not recorded.");
        Require(!imported.IsFavorite, "A fresh import is marked as a favourite.");
        Require(imported.ThumbnailPath is not null && File.Exists(imported.ThumbnailPath),
            "The import did not publish a thumbnail.");

        // The same content is imported once and the original file stays where it was.
        WallpaperAsset duplicate = await library.ImportAsync(fixture.CreateBmp("same-content.bmp", 8, 6));
        Require(duplicate.Id == imported.Id, "Identical content was not deduplicated.");
        Require(library.List().Where(asset => asset.Id == imported.Id).Count() == 1,
            "The duplicate import created a second managed entry.");
        Require(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "The duplicate import changed the original.");
    }

    private static async Task TestThumbnailAsync(Fixture fixture, WallpaperLibrary library)
    {
        string source = fixture.CreateBmp("thumbnail.bmp", 640, 480);
        WallpaperAsset imported = await library.ImportAsync(source);
        string thumbnailPath = imported.ThumbnailPath ??
            throw new InvalidOperationException("The import did not publish a thumbnail.");
        Require(File.Exists(thumbnailPath), "The thumbnail file does not exist.");
        Require(string.Equals(Path.GetDirectoryName(thumbnailPath), Path.GetDirectoryName(imported.Path),
                StringComparison.OrdinalIgnoreCase),
            "The thumbnail is not stored inside the managed asset directory.");

        ImageInfo opaque = await ReadImageAsync(thumbnailPath);
        Require(opaque.Width == 480 && opaque.Height == 360,
            $"The thumbnail is {opaque.Width}x{opaque.Height} instead of 480x360.");
        Require(opaque.MinimumAlpha == 255 && opaque.MaximumAlpha == 255,
            "An opaque source produced a thumbnail with a transparent pixel.");

        // A published thumbnail is reused instead of being encoded again.
        byte[] published = File.ReadAllBytes(thumbnailPath);
        string again = await library.EnsureThumbnailAsync(imported.Id);
        Require(again == thumbnailPath, "The published thumbnail moved to another name.");
        Require(File.ReadAllBytes(thumbnailPath).SequenceEqual(published),
            "The published thumbnail was encoded a second time.");
        File.WriteAllText(thumbnailPath,"damaged thumbnail");string repaired=await library.EnsureThumbnailAsync(imported.Id,CancellationToken.None,rebuild:true);var repairedInfo=await ReadImageAsync(repaired);
        Require(repairedInfo.Width==480&&repairedInfo.Height==360,"A damaged thumbnail could not be rebuilt.");

        // An image with a transparent area keeps its alpha channel in the thumbnail.
        string alphaSource = await fixture.CreateAlphaPngAsync("alpha.png", 64, 48);
        WallpaperAsset alpha = await library.ImportAsync(alphaSource);
        string alphaThumbnail = alpha.ThumbnailPath ??
            throw new InvalidOperationException("The transparent image did not publish a thumbnail.");
        Require(string.Equals(Path.GetExtension(alphaThumbnail), ".png", StringComparison.OrdinalIgnoreCase),
            "A transparent image produced a thumbnail that cannot store an alpha channel.");
        ImageInfo alphaInfo = await ReadImageAsync(alphaThumbnail);
        Require(alphaInfo.Width == 64 && alphaInfo.Height == 48,
            $"The transparent thumbnail is {alphaInfo.Width}x{alphaInfo.Height} instead of 64x48.");
        Require(alphaInfo.MinimumAlpha < 255, "The transparent area of the thumbnail lost its alpha channel.");
        Require(alphaInfo.MaximumAlpha == 255, "The opaque area of the thumbnail lost its opacity.");
    }

    private static async Task TestExifOrientationAsync(Fixture fixture,WallpaperLibrary library)
    {
        using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId,stream);
        byte[] pixels=new byte[12*8*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=(byte)(i%220);pixels[i+1]=90;pixels[i+2]=170;pixels[i+3]=255;}
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,12,8,96,96,pixels);await encoder.FlushAsync();stream.Seek(0);using var reader=new DataReader(stream.GetInputStreamAt(0));await reader.LoadAsync((uint)stream.Size);byte[] jpeg=new byte[stream.Size];reader.ReadBytes(jpeg);
        for(int orientation=1;orientation<=8;orientation++){
            byte[] segment=[255,225,0,34,69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,(byte)orientation,0,0,0,0,0,0,0];
            byte[] combined=new byte[jpeg.Length+segment.Length];jpeg.AsSpan(0,2).CopyTo(combined);segment.CopyTo(combined,2);jpeg.AsSpan(2).CopyTo(combined.AsSpan(2+segment.Length));
            string file=fixture.WriteFile("orientation-"+orientation+".jpg",combined);var asset=await library.ImportAsync(file);var thumbnail=await ReadImageAsync(asset.ThumbnailPath!);
            Require(thumbnail.Width==(orientation>=5?8:12)&&thumbnail.Height==(orientation>=5?12:8),"EXIF orientation "+orientation+" thumbnail was not normalized.");
            Require(Convert.FromBase64String(library.GetDataUri(asset.Id).Split(',')[1]).SequenceEqual(combined),"EXIF import changed original bytes.");
        }
    }

    private static async Task TestLegacyMetadataAsync(Fixture fixture, WallpaperLibrary library)
    {
        string source = fixture.CreateBmp("legacy.bmp", 40, 30);
        WallpaperAsset imported = await library.ImportAsync(source);
        string directory = Path.GetDirectoryName(imported.Path) ??
            throw new InvalidOperationException("The managed asset has no directory.");
        string metadataPath = Path.Combine(directory, "asset.json");
        string legacyImagePath=Path.Combine(directory,"image..bmp");File.Move(imported.Path,legacyImagePath);

        // Rewrite the metadata in the shape used before the product release and drop the thumbnail.
        File.WriteAllText(metadataPath, "{\n" +
            "  \"id\": \"" + imported.Id + "\",\n" +
            "  \"name\": \"legacy-0.6.0.bmp\",\n" +
            "  \"fileName\": \"image..bmp\",\n" +
            "  \"mediaType\": \"image/bmp\",\n" +
            "  \"width\": 40,\n" +
            "  \"height\": 30,\n" +
            "  \"unknownFutureField\": \"keep-me\"\n" +
            "}\n");
        if (imported.ThumbnailPath is not null && File.Exists(imported.ThumbnailPath))
        {
            File.Delete(imported.ThumbnailPath);
        }

        var legacyItems=library.List();
        Require(legacyItems.Any(asset=>asset.Id==imported.Id),"Existing 0.6.0 asset path and metadata were not loaded.");
        WallpaperAsset legacy = legacyItems.Single(asset => asset.Id == imported.Id);
        Require(legacy.Name == "legacy-0.6.0.bmp", "The metadata of an older library was not loaded.");
        Require(legacy.Width == 40 && legacy.Height == 30, "The dimensions of an older library were not loaded.");
        Require(!legacy.IsFavorite, "An older library entry is marked as a favourite.");
        Require(legacy.ThumbnailPath is null, "An older library entry reports a thumbnail that does not exist.");
        Require(legacy.ByteLength == new FileInfo(legacyImagePath).Length,
            "The reported size of an older library entry is wrong.");

        // An older library gets its thumbnail on demand.
        string thumbnail = await library.EnsureThumbnailAsync(imported.Id);
        Require(File.Exists(thumbnail), "The thumbnail of an older library entry was not created.");
        ImageInfo legacyInfo = await ReadImageAsync(thumbnail);
        Require(legacyInfo.Width == 40 && legacyInfo.Height == 30,
            $"The thumbnail of an older library entry is {legacyInfo.Width}x{legacyInfo.Height} instead of 40x30.");
        Require(library.List().Single(asset => asset.Id == imported.Id).ThumbnailPath == thumbnail,
            "The library list did not pick up the thumbnail that was created on demand.");

        // Fields written by a newer version survive a metadata rewrite.
        string metadata = await File.ReadAllTextAsync(metadataPath);
        Require(metadata.Contains("unknownFutureField", StringComparison.Ordinal),
            "An unknown metadata field was dropped.");
        library.Rename(imported.Id, "重命名之后");
        metadata = await File.ReadAllTextAsync(metadataPath);
        Require(metadata.Contains("unknownFutureField", StringComparison.Ordinal),
            "An unknown metadata field was dropped by the rename.");
        Require(metadata.Contains("originalName", StringComparison.Ordinal),
            "The original file name was not kept in the metadata.");
    }

    private static async Task TestRenameAndFavoriteAsync(Fixture fixture, WallpaperLibrary library)
    {
        string source = fixture.CreateBmp("catalog.bmp", 12, 9);
        WallpaperAsset imported = await library.ImportAsync(source);
        string revisionBefore = library.SettingsRevision();

        WallpaperAsset renamed = library.Rename(imported.Id, "  我的壁纸  ");
        Require(renamed.Name == "我的壁纸", "The display name was not normalized.");
        Require(renamed.Id == imported.Id, "The rename changed the asset id.");
        Require(library.List().Single(asset => asset.Id == imported.Id).Name == "我的壁纸",
            "The library list did not observe the rename, the cached description is stale.");
        Require(library.SettingsRevision() == revisionBefore, "A rename changed the settings revision.");

        WallpaperAsset favourite = library.SetFavorite(imported.Id, true);
        Require(favourite.IsFavorite, "The favourite flag was not stored.");
        Require(library.List().Single(asset => asset.Id == imported.Id).IsFavorite,
            "The library list did not observe the favourite, the cached description is stale.");
        Require(library.SettingsRevision() == revisionBefore, "A favourite change changed the settings revision.");
        Require(!library.SetFavorite(imported.Id, false).IsFavorite, "The favourite flag could not be cleared.");
        Require(!library.List().Single(asset => asset.Id == imported.Id).IsFavorite,
            "The library list did not observe the cleared favourite.");

        // The rename only changes the display name, the stored image stays untouched.
        Require(library.GetDataUri(imported.Id).StartsWith("data:image/bmp;base64,", StringComparison.Ordinal),
            "The stored image is no longer readable after a rename.");
        string metadata = await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(imported.Path)!, "asset.json"));
        Require(metadata.Contains("catalog.bmp", StringComparison.Ordinal),
            "The original file name was not kept in the metadata.");

        // Illegal names are refused and leave the entry untouched.
        RequireThrows<ArgumentException>(() => library.Rename(imported.Id, string.Empty));
        RequireThrows<ArgumentException>(() => library.Rename(imported.Id, "   "));
        RequireThrows<ArgumentException>(() => library.Rename(imported.Id, "a/b"));
        RequireThrows<ArgumentException>(() => library.Rename(imported.Id, "a\\b"));
        RequireThrows<ArgumentException>(() => library.Rename(imported.Id, ".."));
        RequireThrows<ArgumentException>(() => library.Rename(imported.Id, new string('名', 101)));
        Require(library.List().Single(asset => asset.Id == imported.Id).Name == "我的壁纸",
            "A refused rename changed the display name.");

        // A deleted entry disappears from the library list.
        library.Delete(imported.Id);
        Require(library.List().All(asset => asset.Id != imported.Id), "The deleted entry is still listed.");
        RequireThrows<FileNotFoundException>(() => library.Rename(imported.Id, "x"),
            "A deleted entry could still be renamed.");
    }

    private static async Task TestCancellationAsync(Fixture fixture, WallpaperLibrary library)
    {
        string source = fixture.CreateBmp("cancelled.bmp", 64, 48);
        string libraryPath = Path.Combine(library.Root, "library");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(() => library.ImportAsync(source, cancelled.Token));
        }

        RequireNoPartialEntries(libraryPath);

        // A cancellation that lands while the image is decoded must not publish a partial entry.
        using (var running = new CancellationTokenSource())
        {
            running.CancelAfter(TimeSpan.FromMilliseconds(5));
            try
            {
                await library.ImportAsync(source, running.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        RequireNoPartialEntries(libraryPath);

        // The same content can still be imported normally afterwards.
        WallpaperAsset imported = await library.ImportAsync(source);
        Require(library.List().Where(asset => asset.Id == imported.Id).Count() == 1,
            "A cancelled import left a partial entry behind.");
        Require(File.ReadAllBytes(imported.Path).Length > 0, "The imported image is empty.");
    }

    private static void TestZoom(WallpaperLibrary library)
    {
        WallpaperLibrary.Validate(new WallpaperSettings { Zoom = 1 });
        WallpaperLibrary.Validate(new WallpaperSettings { Zoom = 3 });
        RequireThrows<ArgumentOutOfRangeException>(() => WallpaperLibrary.Validate(new WallpaperSettings { Zoom = 0.99 }));
        RequireThrows<ArgumentOutOfRangeException>(() => WallpaperLibrary.Validate(new WallpaperSettings { Zoom = 3.01 }));
        RequireThrows<ArgumentOutOfRangeException>(() => WallpaperLibrary.Validate(new WallpaperSettings { Zoom = double.NaN }));
        RequireThrows<ArgumentOutOfRangeException>(() =>
            WallpaperLibrary.Validate(new WallpaperSettings { Zoom = double.PositiveInfinity }));

        library.SaveSettings(library.LoadSettings() with { Zoom = 2.25 }, library.SettingsRevision());
        Require(library.LoadSettings().Zoom == 2.25, "The zoom factor did not round-trip.");

        // A settings file written before the zoom factor existed falls back to the default.
        File.WriteAllText(library.SettingsPath,
            "{\"enabled\":false,\"fit\":\"cover\",\"opacity\":0.28,\"overlay\":0.35}");
        Require(library.LoadSettings().Zoom == 1, "An older settings file did not fall back to the default zoom factor.");
    }

    private static void TestDamagedEntryDoesNotBreakTheList(Fixture fixture, WallpaperLibrary library)
    {
        WallpaperAsset healthy = library.ImportAsync(fixture.CreateBmp("healthy.bmp", 5, 4)).GetAwaiter().GetResult();
        WallpaperAsset broken = library.ImportAsync(fixture.CreateBmp("broken.bmp", 6, 4)).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(broken.Path)!, "asset.json"), "{ not json at all");
        Require(library.UnavailableImageIds().Contains(broken.Id),"Damaged entry was hidden without a repair/cleanup path.");

        IReadOnlyList<WallpaperAsset> assets = library.List();
        Require(assets.Any(asset => asset.Id == healthy.Id), "A damaged entry removed a healthy entry from the list.");
        Require(assets.All(asset => asset.Id != broken.Id), "A damaged entry was returned by the list.");
    }

    private static void RequireNoPartialEntries(string libraryPath)
    {
        if (!Directory.Exists(libraryPath))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(libraryPath))
        {
            string name = Path.GetFileName(entry);
            Require(!name.StartsWith(".import-", StringComparison.Ordinal),
                $"A cancelled import left the staging directory '{name}' behind.");
            Require(!name.EndsWith(".tmp", StringComparison.Ordinal),
                $"A cancelled import left the temporary file '{name}' behind.");
        }
    }

    private static async Task<ImageInfo> ReadImageAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask();
            await writer.FlushAsync().AsTask();
            writer.DetachStream();
        }

        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask();
        BitmapFrame frame = await decoder.GetFrameAsync(0).AsTask();
        PixelDataProvider provider = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask();
        byte[] pixels = provider.DetachPixelData();
        int minimum = 255;
        int maximum = 0;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            minimum = Math.Min(minimum, pixels[index]);
            maximum = Math.Max(maximum, pixels[index]);
        }

        return new ImageInfo(checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), minimum, maximum);
    }

    private sealed record ImageInfo(int Width, int Height, int MinimumAlpha, int MaximumAlpha);

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<T>(Action action, string? message = null) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message ?? "An illegal input was accepted.");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action, string? message = null) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message ?? "An illegal input was accepted.");
    }

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
            string root = Path.Combine(Path.GetTempPath(), "CodexControlCenter.WallpaperCatalogTests",
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

            return WriteFile(name, bytes);
        }

        public async Task<string> CreateAlphaPngAsync(string name, int width, int height)
        {
            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask();
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = ((y * width) + x) * 4;
                    bool opaque = x >= width / 2;
                    pixels[offset] = opaque ? (byte)200 : (byte)0;
                    pixels[offset + 1] = opaque ? (byte)120 : (byte)0;
                    pixels[offset + 2] = opaque ? (byte)40 : (byte)0;
                    pixels[offset + 3] = opaque ? (byte)255 : (byte)0;
                }
            }

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                checked((uint)width),
                checked((uint)height),
                96,
                96,
                pixels);
            await encoder.FlushAsync().AsTask();

            output.Seek(0);
            uint size = checked((uint)output.Size);
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync(size).AsTask();
            byte[] bytes = new byte[checked((int)size)];
            reader.ReadBytes(bytes);
            return WriteFile(name, bytes);
        }

        public string WriteFile(string name, byte[] bytes)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
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
                "CodexControlCenter.WallpaperCatalogTests")) + Path.DirectorySeparatorChar;
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
