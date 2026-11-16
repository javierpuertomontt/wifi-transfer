using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;

namespace WiFiTransfer;

public sealed class FtpBackend : IDisposable
{
    private FtpSettings? _settings;
    private AsyncFtpClient? _browser;
    private AsyncFtpClient? _thumbnails;
    private readonly SemaphoreSlim _browseGate = new(1, 1);
    private readonly SemaphoreSlim _thumbGate = new(1, 1);
    public bool Connected => _browser?.IsConnected == true;
    public string ServerKey => _settings is null ? "" : _settings.Host + ":" + _settings.Port + ":" + _settings.Username;

    private AsyncFtpClient NewClient()
    {
        if (_settings is null) throw new InvalidOperationException("Primero debes conectar el servidor FTP.");
        var config = new FtpConfig
        {
            EncryptionMode = _settings.UseFtps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = 10000,
            ReadTimeout = 20000,
            DataConnectionConnectTimeout = 10000,
            DataConnectionReadTimeout = 30000,
            SocketKeepAlive = true
        };
        return new AsyncFtpClient(_settings.Host, _settings.Username, _settings.Password, _settings.Port, config);
    }

    public async Task ConnectAsync(FtpSettings settings, CancellationToken token)
    {
        _browser?.Dispose();
        _thumbnails?.Dispose();
        _browser = null;
        _thumbnails = null;
        _settings = settings;
        var client = NewClient();
        try
        {
            await client.Connect(token);
            _browser = client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<FileEntry[]> ListAsync(string path, CancellationToken token)
    {
        await _browseGate.WaitAsync(token);
        try
        {
            var client = _browser ?? throw new InvalidOperationException("FTP no conectado.");
            if (!client.IsConnected) await client.Connect(token);
            var entries = await client.GetListing(path, token);
            return entries
                .Where(e => e.Type is FtpObjectType.File or FtpObjectType.Directory)
                .Select(e => new FileEntry
                {
                    Name = e.Name, FullPath = e.FullName,
                    IsDirectory = e.Type == FtpObjectType.Directory,
                    IsLocal = false, Size = Math.Max(e.Size, 0),
                    Modified = e.Modified
                }).ToArray();
        }
        finally { _browseGate.Release(); }
    }

    public async Task DownloadThumbAsync(FileEntry entry, string tempPath, CancellationToken token)
    {
        await _thumbGate.WaitAsync(token);
        try
        {
            _thumbnails ??= NewClient();
            if (!_thumbnails.IsConnected) await _thumbnails.Connect(token);
            var result = await _thumbnails.DownloadFile(tempPath, entry.FullPath, FtpLocalExists.Overwrite, FtpVerify.None, null, token);
            if (result != FtpStatus.Success) throw new IOException("El servidor no entregó la imagen.");
        }
        finally { _thumbGate.Release(); }
    }

    public async Task DownloadAsync(FileEntry entry, string localFile, IProgress<double> progress, CancellationToken token)
    {
        if (File.Exists(localFile)) throw new IOException("Ya existe un archivo con ese nombre: " + localFile);
        var part = localFile + ".wft.part";
        if (File.Exists(part) && entry.Size > 0 && new FileInfo(part).Length > entry.Size) File.Delete(part);
        using var client = NewClient();
        await client.Connect(token);
        var p = new Progress<FtpProgress>(x => { if (x.Progress >= 0) progress.Report(x.Progress); });
        var status = await client.DownloadFile(part, entry.FullPath, FtpLocalExists.Resume, FtpVerify.None, p, token);
        if (status != FtpStatus.Success) throw new IOException("La descarga no se completó: " + status);
        if (entry.Size > 0 && new FileInfo(part).Length != entry.Size)
            throw new IOException("El tamaño descargado difiere del archivo remoto.");
        File.Move(part, localFile);
    }

    public async Task UploadAsync(string localFile, string remoteFile, IProgress<double> progress, CancellationToken token)
    {
        using var client = NewClient();
        await client.Connect(token);
        if (await client.FileExists(remoteFile, token))
            throw new IOException("El archivo ya existe en el Xiaomi: " + remoteFile);
        var part = remoteFile + ".wft-upload";
        if (await client.FileExists(part, token))
            throw new IOException("Ya existe una carga parcial con ese nombre: " + part);
        var p = new Progress<FtpProgress>(x => { if (x.Progress >= 0) progress.Report(x.Progress); });
        var status = await client.UploadFile(localFile, part, FtpRemoteExists.NoCheck, false, FtpVerify.None, p, token);
        if (status != FtpStatus.Success) throw new IOException("No se pudo completar la carga: " + status);
        if (!await client.MoveFile(part, remoteFile, FtpRemoteExists.Skip, token))
            throw new IOException("No se pudo finalizar el archivo remoto (quizás ya existe).");
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken token)
    {
        using var client = NewClient();
        await client.Connect(token);
        await client.CreateDirectory(path, token);
    }

    public static string CombineRemote(string folder, string filename) =>
        (folder == "/" ? "" : folder.TrimEnd('/')) + "/" + filename.TrimStart('/');

    public void Dispose()
    {
        _browser?.Dispose();
        _thumbnails?.Dispose();
        _browseGate.Dispose();
        _thumbGate.Dispose();
    }
}