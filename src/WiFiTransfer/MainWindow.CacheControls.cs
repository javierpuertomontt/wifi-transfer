using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace WiFiTransfer;

public partial class MainWindow
{
    private bool _suspendAutoThumbnails;
    private readonly string _thumbnailErrorsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WiFiTransfer", "miniaturas-errores.txt");

    private void InitializeCacheControls()
    {
        Loaded += async (_, _) => await RefreshCacheUsageAsync();
        ViewThumbnailErrorsButton.IsEnabled = File.Exists(_thumbnailErrorsFile);
    }

    private async Task RefreshCacheUsageAsync()
    {
        try
        {
            var stats = await _cache.GetStatsAsync();
            if (CacheUsageLabel is not null)
                CacheUsageLabel.Text = FileEntry.FormatSize(stats.Bytes) + "  ·  " +
                    stats.Files.ToString("N0") + " miniaturas";
        }
        catch (Exception ex)
        {
            if (CacheUsageLabel is not null) CacheUsageLabel.Text = "No disponible";
            SetStatus("No se pudo leer la caché: " + ex.Message);
        }
    }

    private async void CacheRefresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshCacheUsageAsync();

    private async void CacheClear_Click(object sender, RoutedEventArgs e)
    {
        var current = await _cache.GetStatsAsync();
        if (current.Files == 0)
        {
            await RefreshCacheUsageAsync();
            SetStatus("La caché de miniaturas ya está vacía.");
            return;
        }
        var answer = System.Windows.MessageBox.Show(this,
            "Se eliminarán " + current.Files.ToString("N0") +
            " miniaturas guardadas (" + FileEntry.FormatSize(current.Bytes) + ") en el notebook." +
            "\n\nNo se borrarán fotos del Xiaomi ni los archivos descargados." +
            "\n\nLas miniaturas tendrán que volver a generarse. ¿Continuar?",
            "Limpiar caché de miniaturas", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        CacheClearButton.IsEnabled = false;
        // Stop current producers before clearing. The cache uses a generation counter
        // to reject a stale write that finishes after the clear.
        StopPreload();
        CancelThumbnails();
        _suspendAutoThumbnails = true;
        try
        {
            var result = await _cache.ClearAsync();
            foreach (var entry in _entries) entry.Thumbnail = null;
            RefreshView();
            await RefreshCacheUsageAsync();
            SetStatus("Caché limpiada: " + result.Deleted.ToString("N0") +
                " archivos eliminados." +
                (result.Failed > 0 ? " No se pudieron borrar " + result.Failed + "." : "") +
                " Pulsa «Cargar todas las miniaturas» o actualiza la carpeta para regenerarlas.");
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo limpiar la caché: " + ex.Message);
        }
        finally { CacheClearButton.IsEnabled = true; }
    }

    private async Task WritePreloadErrorsAsync(List<string> errors, int succeeded, int skippedTooLarge)
    {
        try
        {
            var lines = new List<string>
            {
                "WiFi Transfer · Informe de miniaturas · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "Carpeta: " + (_isLocal ? _localPath : _remotePath),
                "Generadas o reutilizadas: " + succeeded,
                "Omitidas por tamaño superior a 60 MB: " + skippedTooLarge,
                "Fallos: " + errors.Count,
                "",
                "Estos fallos no significan que las fotografías originales estén dañadas.",
                "El informe contiene nombres de archivos, nunca credenciales FTP.",
                ""
            };
            lines.AddRange(errors);
            Directory.CreateDirectory(Path.GetDirectoryName(_thumbnailErrorsFile)!);
            await File.WriteAllLinesAsync(_thumbnailErrorsFile, lines);
            ViewThumbnailErrorsButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo guardar el informe de fallos: " + ex.Message);
        }
    }

    private void ViewThumbnailErrors_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_thumbnailErrorsFile))
            {
                SetStatus("Todavía no hay un informe de errores.");
                return;
            }
            Process.Start(new ProcessStartInfo(_thumbnailErrorsFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo abrir el informe: " + ex.Message);
        }
    }
}