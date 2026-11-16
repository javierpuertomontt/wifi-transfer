using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Point = System.Windows.Point;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using DataGridRow = System.Windows.Controls.DataGridRow;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace WiFiTransfer;

public partial class MainWindow
{
    private readonly CultureInfo _spanish = CultureInfo.GetCultureInfo("es-CL");
    private CancellationTokenSource? _preloadCancellation;
    private bool _rebuildingDateFilters;
    private Point _dragOrigin;
    private FileEntry? _dragSourceEntry;
    private bool _dragInProgress;
    private string? _preparedDragKey;
    private string[] _preparedDragPaths = [];
    private readonly string _dragCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WiFiTransfer", "drag-cache");

    private void InitializeFeatures()
    {
        _rebuildingDateFilters = true;
        YearCombo.Items.Add(new ComboBoxItem { Content = "Todos los años", Tag = 0 });
        YearCombo.SelectedIndex = 0;
        MonthCombo.Items.Add(new ComboBoxItem { Content = "Todos los meses", Tag = 0 });
        for (int month = 1; month <= 12; month++)
        {
            var name = _spanish.DateTimeFormat.GetMonthName(month);
            MonthCombo.Items.Add(new ComboBoxItem
            {
                Content = char.ToUpperInvariant(name[0]) + name[1..],
                Tag = month
            });
        }
        MonthCombo.SelectedIndex = 0;
        _rebuildingDateFilters = false;

        try
        {
            Directory.CreateDirectory(_dragCacheRoot);
            _ = Task.Run(() =>
            {
                foreach (var directory in Directory.EnumerateDirectories(_dragCacheRoot))
                {
                    try
                    {
                        if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-2))
                            Directory.Delete(directory, true);
                    }
                    catch { /* These are only disposable files from previous drags. */ }
                }
            });
        }
        catch { /* The drag cache is optional until actually used. */ }
    }

    private void RebuildDateFilters()
    {
        if (YearCombo is null || MonthCombo is null) return;
        _rebuildingDateFilters = true;
        YearCombo.Items.Clear();
        YearCombo.Items.Add(new ComboBoxItem { Content = "Todos los años", Tag = 0 });
        foreach (var year in _entries.Where(x => x.Modified.Year >= 2000)
                                      .Select(x => x.Modified.Year).Distinct().OrderByDescending(x => x))
            YearCombo.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
        YearCombo.SelectedIndex = 0;
        MonthCombo.SelectedIndex = 0;
        _rebuildingDateFilters = false;
    }

    private int SelectedYear => YearCombo.SelectedItem is ComboBoxItem { Tag: int year } ? year : 0;
    private int SelectedMonth => MonthCombo.SelectedItem is ComboBoxItem { Tag: int month } ? month : 0;
    private bool GroupMonths => GroupByMonthCheck.IsChecked == true;

    private void MonthFilters_Changed(object sender, RoutedEventArgs e)
    {
        if (!_rebuildingDateFilters && IsLoaded) RefreshView();
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _visible) entry.IsSelected = true;
        SetStatus(_entries.Count(x => x.IsSelected).ToString("N0") + " archivos seleccionados.");
        UpdateSelectionCount();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries) entry.IsSelected = false;
        DetailsGrid?.UnselectAll();
        _lastClickedIndex = -1;
        UpdateSelectionCount();
        SetStatus("Selección vaciada.");
    }

    private void SelectionCheck_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // A click on the checkbox must not click the enclosing tile.
        if (sender is not CheckBox { Tag: FileEntry entry }) return;
        var index = _visible.IndexOf(entry);
        if (index < 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _lastClickedIndex >= 0)
        {
            // Shift + checkbox always selects the entire contiguous range.
            for (int i = Math.Min(_lastClickedIndex, index); i <= Math.Max(_lastClickedIndex, index); i++)
                _visible[i].IsSelected = true;
        }
        else
        {
            // Regular checkbox clicks toggle only this one entry; Ctrl works the same.
            _lastClickedIndex = index;
        }
        UpdateSelectionCount();
        SetStatus(_entries.Count(x => x.IsSelected).ToString("N0") + " seleccionados.");
    }

    private void UpdateSelectionCount()
    {
        if (CountLabel is null) return;
        var selected = _entries.Count(x => x.IsSelected);
        CountLabel.Text = _visible.Count.ToString("N0") + " elementos"
                        + (selected > 0 ? "  ·  " + selected.ToString("N0") + " seleccionados" : "");
    }

    private void CancelPreload_Click(object sender, RoutedEventArgs e) => StopPreload();

    private void StopPreload()
    {
        _preloadCancellation?.Cancel();
        PreloadButton.IsEnabled = true;
        CancelPreloadButton.Visibility = Visibility.Collapsed;
    }

    private async void Preload_Click(object sender, RoutedEventArgs e)
    {
        if (_preloadCancellation is { IsCancellationRequested: false }) return;
        var photos = _visible.Where(x => x.IsImage && !x.IsDirectory).ToArray();
        if (photos.Length == 0)
        {
            SetStatus("No hay imágenes compatibles con miniaturas en el filtro actual.");
            return;
        }
        if (!_isLocal && !_ftp.Connected)
        {
            SetStatus("Conecta primero el Xiaomi.");
            return;
        }

        long estimatedBytes = photos.Where(x => !x.IsLocal && x.Size > 0 &&
            x.Size <= ThumbnailCache.MaxRemoteThumbnailBytes).Sum(x => x.Size);
        if (estimatedBytes > 1024L * 1024 * 1024)
        {
            var response = System.Windows.MessageBox.Show(this,
                "Para generar miniaturas nuevas el servidor FTP podría transferir hasta " +
                FileEntry.FormatSize(estimatedBytes) +
                " (menos para las imágenes que ya estén en caché). ¿Continuar?",
                "Cargar todas las miniaturas", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (response != MessageBoxResult.Yes) return;
        }

        _preloadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _preloadCancellation = cancellation;
        var token = cancellation.Token;
        _suspendAutoThumbnails = false;
        PreloadButton.IsEnabled = false;
        CancelPreloadButton.Visibility = Visibility.Visible;

        int processed = 0, available = 0, reused = 0, generated = 0, skippedLarge = 0;
        var errors = new List<string>();
        try
        {
            foreach (var photo in photos)
            {
                token.ThrowIfCancellationRequested();

                ThumbnailResult outcome;
                if (photo.Thumbnail is not null)
                    outcome = new ThumbnailResult(ThumbnailOutcome.Cached);
                else
                    outcome = await _cache.PrecacheDetailedAsync(photo, _ftp, token);

                switch (outcome.Outcome)
                {
                    case ThumbnailOutcome.Cached:
                        available++;
                        reused++;
                        break;
                    case ThumbnailOutcome.Generated:
                        available++;
                        generated++;
                        break;
                    case ThumbnailOutcome.TooLarge:
                        skippedLarge++;
                        break;
                    default:
                        var safeReason = outcome.Reason.Replace('\r', ' ').Replace('\n', ' ');
                        errors.Add(photo.Name + ": " +
                            (safeReason.Length > 280 ? safeReason[..280] : safeReason));
                        break;
                }

                processed++;
                PreloadProgressLabel.Text = available.ToString("N0") + " listas / " +
                    photos.Length.ToString("N0") + " · " + processed.ToString("N0") + " revisadas";

                // Update visible tiles without scrolling or materializing thousands of controls.
                if (processed % 12 == 0) RequestVisibleThumbnails();
                if (processed % 40 == 0) await Task.Yield();
            }

            RequestVisibleThumbnails();
            SetStatus("Miniaturas: " + available.ToString("N0") + " disponibles (" +
                generated.ToString("N0") + " nuevas, " + reused.ToString("N0") + " reutilizadas), " +
                skippedLarge.ToString("N0") + " omitidas (>60 MB), " +
                errors.Count.ToString("N0") + " con errores." +
                (errors.Count > 0 ? " Consulta «Ver errores de miniaturas»." : ""));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Carga detenida: " + available.ToString("N0") + " miniaturas disponibles de " +
                processed.ToString("N0") + " revisadas.");
        }
        finally
        {
            if (ReferenceEquals(_preloadCancellation, cancellation))
            {
                _preloadCancellation = null;
                PreloadButton.IsEnabled = true;
                CancelPreloadButton.Visibility = Visibility.Collapsed;
            }
            cancellation.Dispose();
            await WritePreloadErrorsAsync(errors, available, skippedLarge);
            await _cache.TrimCacheAsync();
            await RefreshCacheUsageAsync();
        }
    }

    private static bool OriginatesInCheckBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Tile_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragSourceEntry = null;
        if (OriginatesInCheckBox(e.OriginalSource as DependencyObject)) return;
        if (sender is System.Windows.Controls.Button { Tag: FileEntry entry })
        {
            _dragOrigin = e.GetPosition(this);
            _dragSourceEntry = entry;
        }
    }

    private async void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: FileEntry entry }) return;
        if (!ReferenceEquals(entry, _dragSourceEntry)) return;
        await StartFileDragAsync(sender as DependencyObject ?? this, e);
    }

    private void Details_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragSourceEntry = null;
        var source = e.OriginalSource as DependencyObject;
        var row = ItemsControl.ContainerFromElement(DetailsGrid, source) as DataGridRow;
        if (row?.Item is FileEntry entry)
        {
            _dragOrigin = e.GetPosition(this);
            _dragSourceEntry = entry;
        }
    }

    private async void Details_PreviewMouseMove(object sender, MouseEventArgs e) =>
        await StartFileDragAsync(DetailsGrid, e);

    private async Task StartFileDragAsync(DependencyObject visualSource, MouseEventArgs e)
    {
        if (_dragInProgress || _dragSourceEntry is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var origin = _dragSourceEntry;
        _dragSourceEntry = null;
        var selected = _entries.Where(x => x.IsSelected).ToArray();
        if (!origin.IsSelected || selected.Length == 0) selected = [origin];
        _dragInProgress = true;
        try
        {
            string[] paths;
            if (origin.IsLocal)
                paths = selected.Select(x => x.FullPath).Where(x => File.Exists(x) || Directory.Exists(x)).ToArray();
            else
                paths = await PrepareDragFilesAsync(selected);
            if (paths.Length == 0) return;
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                SetStatus("Archivos preparados. Vuelve a arrastrarlos hacia una carpeta de Windows.");
                return;
            }
            var data = new DataObject(DataFormats.FileDrop, paths);
            System.Windows.DragDrop.DoDragDrop(visualSource, data, DragDropEffects.Copy);
            SetStatus("Archivos listos para copiar en el destino elegido.");
        }
        catch (Exception ex) { SetStatus("No fue posible preparar el arrastre: " + ex.Message); }
        finally { _dragInProgress = false; }
    }

    private async Task<string[]> PrepareDragFilesAsync(FileEntry[] files)
    {
        var key = _ftp.ServerKey + "|" + string.Join("|", files.Select(x => x.FullPath + ":" + x.Size + ":" + x.Modified.Ticks));
        if (string.Equals(key, _preparedDragKey, StringComparison.Ordinal) &&
            _preparedDragPaths.Length == files.Length &&
            _preparedDragPaths.All(x => File.Exists(x) || Directory.Exists(x)))
            return _preparedDragPaths;

        var folder = Path.Combine(_dragCacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var job = new TransferJob { Name = "Preparando arrastre FTP", Direction = "Descarga temporal" };
        _jobs.Insert(0, job);
        var prepared = new List<string>();
        int i = 0;
        foreach (var file in files)
        {
            job.Status = "Preparando " + (++i) + " / " + files.Length;
            // Stage the remote bytes, then expose ordinary Windows paths to Explorer.
            // The cache is deleted only on a future application start after two days.
            await DownloadEntryAsync(file, folder, job, CancellationToken.None);
            prepared.Add(Path.Combine(folder, ValidateDropName(file.Name)));
        }
        job.Progress = 100;
        job.Status = "Preparado";
        _preparedDragKey = key;
        _preparedDragPaths = prepared.ToArray();
        return _preparedDragPaths;
    }

    private static string ValidateDropName(string value)
    {
        if (value is "." or ".." || value.Length == 0 || value.IndexOfAny(Path.GetInvalidFileNameChars()) != -1 ||
            value.Contains('/') || value.Contains('\\'))
            throw new IOException("Nombre de archivo no válido.");
        return value;
    }

    private void Explorer_DragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles && (_isLocal || _ftp.Connected) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Explorer_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (!_isLocal && !_ftp.Connected) { SetStatus("Conecta el Xiaomi para subir archivos."); return; }

        var targetRemote = _remotePath;
        var targetLocal = _localPath;
        foreach (var file in files)
        {
            if (!File.Exists(file) && !Directory.Exists(file)) continue;
            if (!_isLocal)
                ScheduleTransfer(Path.GetFileName(file), "Subiendo", async (job, token) =>
                    await UploadEntryAsync(file, targetRemote, job, token));
            else
                ScheduleTransfer(Path.GetFileName(file), "Copiando", async (job, token) =>
                    await CopyToLocalAsync(file, targetLocal, token));
        }
        SetStatus(files.Length + " archivo(s) añadidos a la cola de transferencia.");
    }

    private static Task CopyToLocalAsync(string source, string destination, CancellationToken token) =>
        Task.Run(() =>
        {
            var name = ValidateDropName(Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
            var target = Path.Combine(destination, name);
            if (File.Exists(source))
            {
                File.Copy(source, target, false); // Never silently overwrite an existing file.
                return;
            }
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Ya existe: " + target);
            if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Acceso directo a carpeta no permitido.");
            Directory.CreateDirectory(target);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var item in Directory.EnumerateFiles(source, "*", options))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, item);
                var output = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(item, output, false);
            }
        }, token);
}
