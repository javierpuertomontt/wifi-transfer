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

public enum ThumbnailOutcome { Cached, Generated, TooLarge, Failed }

public sealed record ThumbnailResult(ThumbnailOutcome Outcome, string Reason = "");

public sealed record ThumbnailCacheStats(long Bytes, int Files);

public sealed record ThumbnailClearResult(int Deleted, int Failed);

public sealed class ThumbnailCache
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WiFiTransfer", "thumbnails");
    public const long MaxRemoteThumbnailBytes = 60L * 1024 * 1024;

    private readonly SemaphoreSlim _cacheWriteGate = new(1, 1);
    private int _cacheEpoch;

    public ThumbnailCache()
    {
        Directory.CreateDirectory(_directory);
        try { TrimCache(); } catch { /* The cache is optional. */ }
    }

    public string DirectoryPath => _directory;

    private string CachePath(string key) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");

    private string CachePath(FileEntry entry, FtpBackend backend)
    {
        var key = entry.IsLocal ? "local|" + entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks
                                : backend.ServerKey + "|" + entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks;
        return CachePath(key);
    }

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

    public async Task<ThumbnailResult> PrecacheDetailedAsync(FileEntry entry, FtpBackend backend, CancellationToken token)
    {
        if (!entry.IsImage) return new ThumbnailResult(ThumbnailOutcome.Failed, "Formato no compatible");
        if (!entry.IsLocal && entry.Size > MaxRemoteThumbnailBytes)
            return new ThumbnailResult(ThumbnailOutcome.TooLarge, "Supera el límite de 60 MB por foto");

        var cache = CachePath(entry, backend);
        if (File.Exists(cache))
        {
            try
            {
                await Task.Run(() => ReadImage(cache, 256), token);
                return new ThumbnailResult(ThumbnailOutcome.Cached);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                try { File.Delete(cache); } catch { }
            }
        }

        try
        {
            await LoadCoreAsync(entry, backend, cache, token, reportFailures: true);
            return File.Exists(cache) && new FileInfo(cache).Length > 0
                ? new ThumbnailResult(ThumbnailOutcome.Generated)
                : new ThumbnailResult(ThumbnailOutcome.Failed, "No fue posible guardar en disco la miniatura");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var reason = ex is FileFormatException or NotSupportedException or System.Runtime.InteropServices.COMException
                ? "Formato no decodificable: " + ex.Message
                : ex.GetType().Name + ": " + ex.Message;
            return new ThumbnailResult(ThumbnailOutcome.Failed, reason);
        }
    }

    public Task<ImageSource?> LoadAsync(FileEntry entry, FtpBackend backend, CancellationToken token)
    {
        if (!entry.IsImage || (!entry.IsLocal && entry.Size > MaxRemoteThumbnailBytes))
            return Task.FromResult<ImageSource?>(null);
        return LoadCoreAsync(entry, backend, CachePath(entry, backend), token, reportFailures: false);
    }

    private async Task<ImageSource?> LoadCoreAsync(
        FileEntry entry, FtpBackend backend, string cache, CancellationToken token, bool reportFailures)
    {
        if (File.Exists(cache))
        {
            try { return await Task.Run(() => (ImageSource)ReadImage(cache, 256), token); }
            catch (OperationCanceledException) { throw; }
            catch { try { File.Delete(cache); } catch { } }
        }

        var source = entry.FullPath;
        string? remoteTemp = null;
        var epoch = Volatile.Read(ref _cacheEpoch);
        try
        {
            if (!entry.IsLocal)
            {
                remoteTemp = Path.Combine(Path.GetTempPath(), "wft-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
                await backend.DownloadThumbAsync(entry, remoteTemp, token);
                source = remoteTemp;
            }

            var decoded = await Task.Run(() => ReadImage(source, 256), token);
            token.ThrowIfCancellationRequested();

            // Writing and cache clearing share one lock. Stale downloads never repopulate a cleared cache.
            await _cacheWriteGate.WaitAsync(token);
            try
            {
                if (epoch == Volatile.Read(ref _cacheEpoch))
                {
                    string? temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        var encoder = new JpegBitmapEncoder { QualityLevel = 78 };
                        encoder.Frames.Add(BitmapFrame.Create(decoded));
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            encoder.Save(file);
                        File.Move(temporary, cache, true);
                    }
                    catch
                    {
                        if (reportFailures) throw;
                    }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    }
                }
            }
            finally { _cacheWriteGate.Release(); }
            return decoded;
        }
        catch (OperationCanceledException) { throw; }
        catch when (!reportFailures) { return null; }
        finally
        {
            if (remoteTemp is not null)
                try { File.Delete(remoteTemp); } catch { }
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

    public Task<ThumbnailCacheStats> GetStatsAsync() => Task.Run(() =>
    {
        long total = 0;
        int count = 0;
        try
        {
            foreach (var name in Directory.EnumerateFiles(_directory, "*.jpg"))
            {
                try
                {
                    total += new FileInfo(name).Length;
                    count++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new ThumbnailCacheStats(total, count);
    });

    public async Task<ThumbnailClearResult> ClearAsync()
    {
        // Invalidate any active remote download that has not written its thumbnail yet.
        Interlocked.Increment(ref _cacheEpoch);
        await _cacheWriteGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                int deleted = 0, failed = 0;
                foreach (var path in Directory.EnumerateFiles(_directory))
                {
                    // Dedicated folder: only remove our hashed JPEG thumbnails and abandoned temp files.
                    var name = Path.GetFileName(path);
                    if (!name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(path); deleted++; }
                    catch (IOException) { failed++; }
                    catch (UnauthorizedAccessException) { failed++; }
                }
                return new ThumbnailClearResult(deleted, failed);
            });
        }
        finally { _cacheWriteGate.Release(); }
    }

    public Task TrimCacheAsync() => Task.Run(() =>
    {
        _cacheWriteGate.Wait();
        try { TrimCache(); }
        finally { _cacheWriteGate.Release(); }
    });

    private void TrimCache()
    {
        const long cap = 1024L * 1024 * 1024;
        var files = Directory.GetFiles(_directory, "*.jpg")
            .Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > cap) try { file.Delete(); } catch { }
        }
    }
}