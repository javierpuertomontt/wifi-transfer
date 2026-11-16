using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WiFiTransfer;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(144d));
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(150d));
    public static readonly DependencyProperty ThumbHeightProperty = DependencyProperty.Register(nameof(ThumbHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(98d));
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public double ThumbHeight { get => (double)GetValue(ThumbHeightProperty); set => SetValue(ThumbHeightProperty, value); }

    private readonly FtpBackend _ftp = new();
    private readonly ThumbnailCache _cache = new();
    private readonly ObservableCollection<TransferJob> _jobs = [];
    private readonly SemaphoreSlim _transfers = new(2, 2);
    private readonly HashSet<string> _loadingThumbnails = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private CancellationTokenSource _navigation = new();
    private CancellationTokenSource _thumbCancellation = new();
    private List<FileEntry> _entries = [];
    private List<FileEntry> _visible = [];
    private string _remotePath = "/";
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private string _destination = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private bool _isLocal = true;
    private int _lastClickedIndex = -1;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WiFiTransfer", "connection.json");

    public MainWindow()
    {
        InitializeComponent();
        TransfersList.ItemsSource = _jobs;
        DestinationLabel.Text = _destination;
        LoadSettings();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshView(); };
        _scrollTimer.Tick += (_, _) => { _scrollTimer.Stop(); RequestVisibleThumbnails(); };
        TileRowsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(GridScrolled));
        Loaded += async (_, _) => await BrowseLocalAsync(_localPath);
        Closed += (_, _) =>
        {
            _navigation.Cancel();
            _thumbCancellation.Cancel();
            _ftp.Dispose();
        };
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("host", out var host)) HostBox.Text = host.GetString() ?? "";
            if (root.TryGetProperty("port", out var port)) PortBox.Text = port.GetInt32().ToString();
            if (root.TryGetProperty("user", out var user)) UserBox.Text = user.GetString() ?? "";
            if (root.TryGetProperty("ftps", out var secure)) FtpsCheck.IsChecked = secure.GetBoolean();
        }
        catch { /* Invalid settings should not prevent startup. */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var data = new { host = HostBox.Text.Trim(), port = int.Parse(PortBox.Text), user = UserBox.Text.Trim(), ftps = FtpsCheck.IsChecked == true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(data));
        }
        catch { /* Settings are optional; passwords are never persisted. */ }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && (uri.Scheme == "ftp" || uri.Scheme == "ftps"))
        {
            host = uri.Host;
            if (!uri.IsDefaultPort) PortBox.Text = uri.Port.ToString();
        }
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError("Indica una IP o nombre de servidor y un puerto válido (1 a 65535).");
            return;
        }
        ConnectButton.IsEnabled = false;
        ConnectionStatus.Text = "Conectando…";
        try
        {
            var settings = new FtpSettings(host, port, string.IsNullOrWhiteSpace(UserBox.Text) ? "anonymous" : UserBox.Text.Trim(), PassBox.Password, FtpsCheck.IsChecked == true);
            await _ftp.ConnectAsync(settings, CancellationToken.None);
            HostBox.Text = host;
            SaveSettings();
            ConnectionStatus.Text = "● Conectado";
            _remotePath = "/";
            await BrowseRemoteAsync("/");
            SetStatus("Conexión FTP correcta. Puedes navegar por las carpetas del Xiaomi.");
        }
        catch (Exception ex)
        {
            ConnectionStatus.Text = "Desconectado";
            ShowError("No se pudo conectar con el servidor FTP: " + ex.Message);
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async Task BrowseRemoteAsync(string path)
    {
        if (!_ftp.Connected) { SetStatus("Conecta primero tu servidor FTP."); return; }
        ResetNavigation();
        var token = _navigation.Token;
        _isLocal = false;
        _remotePath = NormalizeRemote(path);
        PathLabel.Text = "📱 Xiaomi  /  " + _remotePath;
        SetStatus("Leyendo carpeta del Xiaomi…");
        try
        {
            var items = await _ftp.ListAsync(_remotePath, token);
            if (token.IsCancellationRequested) return;
            _entries = items.ToList();
            RefreshView();
            SetStatus("Carpeta cargada: " + _entries.Count.ToString("N0") + " elementos.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("No se pudo abrir la carpeta: " + ex.Message); }
    }

    private async Task BrowseLocalAsync(string path)
    {
        ResetNavigation();
        var token = _navigation.Token;
        _isLocal = true;
        _localPath = path;
        PathLabel.Text = "💻 Este equipo  /  " + _localPath;
        SetStatus("Leyendo carpeta del computador…");
        try
        {
            var items = await Task.Run(() =>
            {
                var list = new List<FileEntry>();
                foreach (var name in Directory.EnumerateFileSystemEntries(path))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attr = File.GetAttributes(name);
                        var directory = (attr & FileAttributes.Directory) != 0;
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        var info = directory ? (FileSystemInfo)new DirectoryInfo(name) : new FileInfo(name);
                        list.Add(new FileEntry
                        {
                            Name = Path.GetFileName(name), FullPath = name,
                            IsDirectory = directory, IsLocal = true,
                            Size = directory ? 0 : ((FileInfo)info).Length, Modified = info.LastWriteTime
                        });
                    }
                    catch { /* Skip inaccessible entries. */ }
                }
                return list;
            }, token);
            if (token.IsCancellationRequested) return;
            _entries = items;
            RefreshView();
            SetStatus(_entries.Count.ToString("N0") + " elementos en el computador.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("No se pudo explorar el computador: " + ex.Message); }
    }

    private void ResetNavigation()
    {
        _navigation.Cancel();
        _navigation.Dispose();
        _navigation = new CancellationTokenSource();
        CancelThumbnails();
        _lastClickedIndex = -1;
    }

    private void CancelThumbnails()
    {
        _thumbCancellation.Cancel();
        _thumbCancellation.Dispose();
        _thumbCancellation = new CancellationTokenSource();
        _loadingThumbnails.Clear();
    }

    private void RefreshView()
    {
        if (!IsLoaded || ViewCombo is null || SortCombo is null || SearchBox is null) return;
        var search = SearchBox.Text.Trim();
        var query = _entries.Where(x => search.Length == 0 || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        var sorted = SortCombo.SelectedIndex switch
        {
            1 => query.OrderByDescending(x => x.IsDirectory).ThenByDescending(x => x.Modified).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            2 => query.OrderByDescending(x => x.IsDirectory).ThenByDescending(x => x.Size).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };
        _visible = sorted.ToList();
        CountLabel.Text = _visible.Count.ToString("N0") + " elementos";

        var mode = ViewCombo.SelectedIndex;
        TileWidth = mode switch { 0 => 208d, 1 => 175d, 2 => 144d, 3 => 108d, _ => 144d };
        ThumbHeight = mode switch { 0 => 160d, 1 => 124d, 2 => 98d, 3 => 70d, _ => 98d };
        TileHeight = ThumbHeight + 54d;
        var detailed = mode is 4 or 5;
        DetailsGrid.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        TileRowsList.Visibility = detailed ? Visibility.Collapsed : Visibility.Visible;
        if (detailed)
        {
            for (var i = 1; i < DetailsGrid.Columns.Count; i++)
                DetailsGrid.Columns[i].Visibility = mode == 4 ? Visibility.Collapsed : Visibility.Visible;
            DetailsGrid.HeadersVisibility = mode == 4 ? DataGridHeadersVisibility.None : DataGridHeadersVisibility.Column;
            DetailsGrid.ItemsSource = _visible;
            return;
        }
        DetailsGrid.ItemsSource = null;
        var columns = Math.Max(1, (int)(Math.Max(220, TileRowsList.ActualWidth - 26) / (TileWidth + 6)));
        var rows = new List<FileRow>();
        for (var i = 0; i < _visible.Count; i += columns)
            rows.Add(new FileRow { Files = _visible.Skip(i).Take(columns).ToList() });
        TileRowsList.ItemsSource = rows;
        _scrollTimer.Stop();
        _scrollTimer.Start();
    }

    private void TileRows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && IsLoaded && _entries.Count > 0 && ViewCombo.SelectedIndex < 4)
            RefreshView();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer is null) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshView();

    private void FileTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: FileEntry entry }) return;
        var index = _visible.IndexOf(entry);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _lastClickedIndex >= 0)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                foreach (var item in _entries) item.IsSelected = false;
            for (var i = Math.Min(index, _lastClickedIndex); i <= Math.Max(index, _lastClickedIndex); i++)
                _visible[i].IsSelected = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            entry.IsSelected = !entry.IsSelected;
            _lastClickedIndex = index;
        }
        else
        {
            foreach (var item in _entries) item.IsSelected = ReferenceEquals(item, entry);
            _lastClickedIndex = index;
        }
        SetStatus(_entries.Count(x => x.IsSelected) + " seleccionados.");
    }

    private async void FileTile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: FileEntry entry })
        {
            e.Handled = true;
            if (entry.IsDirectory) await OpenFolderAsync(entry);
            else if (entry.IsImage) await OpenPreviewAsync(entry);
        }
    }

    private async void Details_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailsGrid.SelectedItem is FileEntry entry)
        {
            if (entry.IsDirectory) await OpenFolderAsync(entry);
            else if (entry.IsImage) await OpenPreviewAsync(entry);
        }
    }

    private void Details_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems.OfType<FileEntry>()) item.IsSelected = false;
        foreach (var item in e.AddedItems.OfType<FileEntry>()) item.IsSelected = true;
    }

    private async Task OpenPreviewAsync(FileEntry entry)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = entry.Thumbnail, Stretch = Stretch.Uniform, Margin = new Thickness(14)
        };
        var panel = new DockPanel();
        var title = new TextBlock
        {
            Text = entry.Name + "   ·   " + entry.SizeText,
            Margin = new Thickness(18, 12, 18, 0), FontSize = 15,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["AppForeground"]
        };
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        panel.Children.Add(image);
        var viewer = new Window
        {
            Title = "Vista previa · " + entry.Name, Owner = this, Width = 1000, Height = 690,
            MinWidth = 520, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["AppBackground"],
            Content = panel
        };
        viewer.Show();
        try
        {
            var full = await _cache.LoadPreviewAsync(entry, _ftp, CancellationToken.None);
            if (viewer.IsVisible && full is not null) image.Source = full;
        }
        catch (Exception ex)
        {
            if (viewer.IsVisible) title.Text += "   ·   Vista ampliada no disponible: " + ex.Message;
        }
    }

    private Task OpenFolderAsync(FileEntry entry) =>
        entry.IsLocal ? BrowseLocalAsync(entry.FullPath) : BrowseRemoteAsync(entry.FullPath);

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocal)
        {
            var parent = Directory.GetParent(_localPath);
            if (parent is not null) await BrowseLocalAsync(parent.FullName);
        }
        else
        {
            var current = _remotePath.TrimEnd('/');
            var index = current.LastIndexOf('/');
            await BrowseRemoteAsync(index <= 0 ? "/" : current[..index]);
        }
    }

    private static string NormalizeRemote(string path) => "/" + path.Trim().Trim('/');

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocal) await BrowseLocalAsync(_localPath);
        else await BrowseRemoteAsync(_remotePath);
    }

    private async void Remote_Click(object sender, RoutedEventArgs e) => await BrowseRemoteAsync(_remotePath);
    private async void Local_Click(object sender, RoutedEventArgs e) => await BrowseLocalAsync(_localPath);

    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Selecciona la carpeta del notebook donde descargar archivos",
            SelectedPath = _destination,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _destination = dialog.SelectedPath;
            DestinationLabel.Text = _destination;
        }
    }

    private void FileTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: FileEntry entry }) QueueThumbnail(entry);
    }

    private void GridScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || TileRowsList.Visibility != Visibility.Visible) return;
        CancelThumbnails();
        _scrollTimer.Stop();
        _scrollTimer.Start();
    }

    private void RequestVisibleThumbnails()
    {
        if (TileRowsList.Visibility != Visibility.Visible) return;
        WalkTiles(TileRowsList);
    }

    private void WalkTiles(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Button { Tag: FileEntry item } button && button.IsVisible) QueueThumbnail(item);
            else WalkTiles(child);
        }
    }

    private void QueueThumbnail(FileEntry entry)
    {
        if (!entry.IsImage || entry.Thumbnail is not null || entry.IsLocal != _isLocal) return;
        if (!_isLocal && !_ftp.Connected) return;
        var key = (entry.IsLocal ? "pc|" : "ftp|") + entry.FullPath;
        if (!_loadingThumbnails.Add(key)) return;
        var token = _thumbCancellation.Token;
        _ = LoadThumbnailAsync(entry, key, token);
    }

    private async Task LoadThumbnailAsync(FileEntry entry, string key, CancellationToken token)
    {
        try
        {
            var image = await _cache.LoadAsync(entry, _ftp, token);
            if (!token.IsCancellationRequested && image is not null) entry.Thumbnail = image;
        }
        catch (OperationCanceledException) { }
        catch { /* Thumbnail failures must never block the explorer. */ }
        finally { _loadingThumbnails.Remove(key); }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocal) { SetStatus("Selecciona Xiaomi para descargar. Usa 'Subir archivos' desde el PC."); return; }
        var chosen = _entries.Where(x => x.IsSelected).ToArray();
        if (chosen.Length == 0) { SetStatus("Selecciona uno o varios archivos o carpetas del Xiaomi."); return; }
        foreach (var entry in chosen) ScheduleTransfer(entry.Name, "Descargando", async (job, token) =>
            await DownloadEntryAsync(entry, _destination, job, token));
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!_ftp.Connected) { ShowError("Conecta el Xiaomi antes de subir archivos."); return; }
        if (_isLocal && _entries.Any(x => x.IsSelected))
        {
            foreach (var entry in _entries.Where(x => x.IsSelected).ToArray())
                ScheduleTransfer(entry.Name, "Subiendo", async (job, token) =>
                    await UploadEntryAsync(entry.FullPath, _remotePath, job, token));
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Selecciona los archivos para enviar al Xiaomi" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
            ScheduleTransfer(Path.GetFileName(file), "Subiendo", async (job, token) =>
                await UploadEntryAsync(file, _remotePath, job, token));
    }

    private void ScheduleTransfer(string name, string direction, Func<TransferJob, CancellationToken, Task> operation)
    {
        var job = new TransferJob { Name = name, Direction = direction };
        _jobs.Insert(0, job);
        _ = Task.Run(async () =>
        {
            await _transfers.WaitAsync();
            try
            {
                await Dispatcher.InvokeAsync(() => job.Status = direction + "…");
                await operation(job, CancellationToken.None);
                await Dispatcher.InvokeAsync(() => { job.Progress = 100; job.Status = "Completado"; });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => job.Status = "Error: " + ex.Message);
            }
            finally { _transfers.Release(); }
        });
    }

    private async Task DownloadEntryAsync(FileEntry entry, string destFolder, TransferJob job, CancellationToken token)
    {
        if (entry.IsDirectory)
        {
            var folder = Path.Combine(destFolder, SafeName(entry.Name));
            Directory.CreateDirectory(folder);
            foreach (var child in await _ftp.ListAsync(entry.FullPath, token))
                await DownloadEntryAsync(child, folder, job, token);
            return;
        }
        Directory.CreateDirectory(destFolder);
        var filename = Path.Combine(destFolder, SafeName(entry.Name));
        var progress = new Progress<double>(x => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (job.Status != "Completado") { job.Progress = x; job.Status = "Descargando " + x.ToString("0") + "%"; }
        })));
        await RetryAsync(() => _ftp.DownloadAsync(entry, filename, progress, token), token);
    }

    private async Task UploadEntryAsync(string localPath, string remoteFolder, TransferJob job, CancellationToken token)
    {
        if (Directory.Exists(localPath))
        {
            var remote = FtpBackend.CombineRemote(remoteFolder, Path.GetFileName(localPath));
            await _ftp.CreateDirectoryAsync(remote, token);
            var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFileSystemEntries(localPath, "*", opts))
                await UploadEntryAsync(path, remote, job, token);
            return;
        }
        var remoteFile = FtpBackend.CombineRemote(remoteFolder, Path.GetFileName(localPath));
        var progress = new Progress<double>(x => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (job.Status != "Completado") { job.Progress = x; job.Status = "Subiendo " + x.ToString("0") + "%"; }
        })));
        await RetryAsync(() => _ftp.UploadAsync(localPath, remoteFile, progress, token), token);
    }

    private static async Task RetryAsync(Func<Task> action, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await action(); return; }
            catch (Exception ex) when (attempt < 2 && ex is not IOException && ex is not UnauthorizedAccessException)
            { await Task.Delay((attempt + 1) * 1200, token); }
        }
    }

    private static string SafeName(string name)
    {
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new IOException("Nombre de archivo remoto no válido: " + name);
        return name;
    }

    private void SetStatus(string message) => StatusLabel.Text = message;
    private void ShowError(string message)
    {
        SetStatus(message);
        System.Windows.MessageBox.Show(this, message, "WiFi Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}