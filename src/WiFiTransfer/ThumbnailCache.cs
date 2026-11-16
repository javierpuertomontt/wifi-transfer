using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WiFiTransfer;

public sealed class ThumbnailCache
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WiFiTransfer", "thumbnails");
    private const long MaxRemoteThumbnailBytes = 25L * 1024 * 1024;

    public ThumbnailCache()
    {
        Directory.CreateDirectory(_directory);
        try { TrimCache(); } catch { /* Cached images are optional. */ }
    }

    private string CachePath(string key) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");

    private static BitmapSource ReadImage(string path, int pixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = pixelWidth;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();
        return image;
    }

    public async Task<bool> PrecacheAsync(FileEntry entry, FtpBackend backend, CancellationToken token)
    {
        // Autocarga en disco, sin retener miles de BitmapSource en memoria.
        if (!entry.IsImage || (!entry.IsLocal && entry.Size > MaxRemoteThumbnailBytes)) return false;
        var key = entry.IsLocal ? "local|" + entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks
                                : backend.ServerKey + "|" + entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks;
        var cacheFile = CachePath(key);
        if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0) return true;
        return await LoadAsync(entry, backend, token) is not null;
    }

    public Task<ImageSource?> LoadAsync(FileEntry entry, FtpBackend backend, CancellationToken token)
    {
        if (!entry.IsImage) return Task.FromResult<ImageSource?>(null);
        if (!entry.IsLocal && entry.Size > MaxRemoteThumbnailBytes) return Task.FromResult<ImageSource?>(null);
        var key = entry.IsLocal ? "local|" + entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks
                                : backend.ServerKey + "|" + entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks;
        var cache = CachePath(key);
        return LoadCoreAsync(entry, backend, cache, token);
    }

    private static async Task<ImageSource?> LoadCoreAsync(FileEntry entry, FtpBackend backend, string cache, CancellationToken token)
    {
        if (File.Exists(cache))
        {
            try { return await Task.Run(() => (ImageSource)ReadImage(cache, 256), token); }
            catch { try { File.Delete(cache); } catch { } }
        }

        var source = entry.FullPath;
        string? temporary = null;
        try
        {
            if (!entry.IsLocal)
            {
                temporary = Path.Combine(Path.GetTempPath(), "wft-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
                await backend.DownloadThumbAsync(entry, temporary, token);
                source = temporary;
            }

            var thumbnail = await Task.Run<ImageSource?>(() =>
            {
                token.ThrowIfCancellationRequested();
                var decoded = ReadImage(source, 256);
                try
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 78 };
                    encoder.Frames.Add(BitmapFrame.Create(decoded));
                    using var output = new FileStream(cache + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);
                    encoder.Save(output);
                    output.Flush();
                    output.Close();
                    File.Move(cache + ".tmp", cache, true);
                }
                catch { /* The in-memory thumbnail still works if caching fails. */ }
                return decoded;
            }, token);
            return thumbnail;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { }
        }
    }

    public async Task<ImageSource?> LoadPreviewAsync(FileEntry entry, FtpBackend backend, CancellationToken token)
    {
        if (!entry.IsImage) return null;
        if (!entry.IsLocal && entry.Size > 100L * 1024 * 1024)
            throw new IOException("Imagen demasiado grande para una vista ampliada remota.");
        var source = entry.FullPath;
        string? temporary = null;
        try
        {
            if (!entry.IsLocal)
            {
                temporary = Path.Combine(Path.GetTempPath(), "wft-preview-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
                await backend.DownloadThumbAsync(entry, temporary, token);
                source = temporary;
            }
            return await Task.Run<ImageSource>(() => ReadImage(source, 1600), token);
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { }
        }
    }

    private void TrimCache()
    {
        const long cap = 1024L * 1024 * 1024;
        var files = Directory.GetFiles(_directory, "*.jpg").Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > cap) try { file.Delete(); } catch { }
        }
    }
}