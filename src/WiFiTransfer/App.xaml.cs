using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WiFiTransfer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
        base.OnExit(e);
    }

    private void OnPreferencesChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    private void ApplyTheme()
    {
        var isDark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            isDark = Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1) ?? 1) == 0;
        }
        catch { /* Use light theme if Windows registry is unavailable. */ }

        Resources["AppBackground"] = Brush(isDark ? "#121820" : "#F7F8FA");
        Resources["AppForeground"] = Brush(isDark ? "#F0F4FB" : "#202630");
        Resources["PanelBackground"] = Brush(isDark ? "#1C2633" : "#FFFFFF");
        Resources["BorderBrushSoft"] = Brush(isDark ? "#344154" : "#DDE2EA");
        Resources["MutedForeground"] = Brush(isDark ? "#A8BACB" : "#64748B");
        Resources["AccentBrush"] = Brush(isDark ? "#72A7FF" : "#2563EB");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}