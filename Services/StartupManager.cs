using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MacroDeck.Server.Services;

/// <summary>
/// Windows açılışında uygulamanın arka planda otomatik başlatılmasını yönetir.
/// Registry: HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MacroDeckServer";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var value = key?.GetValue(AppName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath 
                    ?? Process.GetCurrentProcess().MainModule?.FileName 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MacroDeck.Server.exe");

                // Windows açılışında arka planda (tepside) başlaması için --minimized argümanı eklenir
                string command = $"\"{exePath}\" --minimized";
                key.SetValue(AppName, command);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupManager] Hata: {ex.Message}");
        }
    }
}
