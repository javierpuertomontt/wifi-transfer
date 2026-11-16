using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WiFiTransfer;

public sealed record FtpSettings(string Host, int Port, string Username, string Password, bool UseFtps);

public sealed class FileEntry : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isSelected;
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsLocal { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public string Icon => IsDirectory ? "📁" : IsImage ? "🖼" : IsVideo ? "🎞" : IsPdf ? "📕" : "📄";
    public bool IsImage => !IsDirectory && ImageExtensions.Contains(Path.GetExtension(Name).ToLowerInvariant());
    public bool IsVideo => !IsDirectory && VideoExtensions.Contains(Path.GetExtension(Name).ToLowerInvariant());
    public bool IsPdf => !IsDirectory && Path.GetExtension(Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public string TypeText => IsDirectory ? "Carpeta" : (Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "Archivo");
    public string SizeText => IsDirectory ? "—" : FormatSize(Size);
    public string ModifiedText => Modified <= DateTime.MinValue.AddDays(1) ? "—" : Modified.ToString("dd-MM-yyyy HH:mm");
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; Changed(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var v = (double)bytes;
        var n = -1;
        do { v /= 1024; n++; } while (v >= 1024 && n < units.Length - 1);
        return v.ToString("0.#") + " " + units[n];
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".3gp" };
}

public sealed class FileRow
{
    public List<FileEntry> Files { get; init; } = [];
}

public sealed class TransferJob : INotifyPropertyChanged
{
    private double _progress;
    private string _status = "En espera";
    public string Name { get; init; } = "";
    public string Direction { get; init; } = "";
    public double Progress { get => _progress; set { _progress = value; Notify(nameof(Progress)); } }
    public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}