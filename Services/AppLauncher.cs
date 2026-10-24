using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MacroDeck.Server.Services;

public static class AppLauncher
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Bir komut satırı dizesini (.exe yolu ve varsa parametreler) güvenli bir şekilde ayrıştırır.
    /// Örn: "C:\Program Files\SteelSeries\GG\SteelSeriesGGEZ.exe" -dataPath="C:\ProgramData\SteelSeries\GG" -dbEnv=production
    /// </summary>
    public static (string exePath, string arguments) SplitCommandLine(string commandLine, string? explicitArgs = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (string.Empty, explicitArgs ?? string.Empty);

        string trimmed = commandLine.Trim();
        string exe = trimmed;
        string args = explicitArgs?.Trim() ?? string.Empty;

        // Durum 1: Tırnak ile başlıyorsa: "C:\Program Files\...\app.exe" -arg1 -arg2
        if (trimmed.StartsWith("\""))
        {
            int closingQuoteIndex = trimmed.IndexOf('\"', 1);
            if (closingQuoteIndex > 0)
            {
                exe = trimmed.Substring(1, closingQuoteIndex - 1).Trim();
                string embeddedArgs = trimmed.Substring(closingQuoteIndex + 1).Trim();
                if (!string.IsNullOrWhiteSpace(embeddedArgs))
                {
                    args = string.IsNullOrWhiteSpace(args) ? embeddedArgs : $"{embeddedArgs} {args}";
                }
            }
            else
            {
                exe = trimmed.Trim('\"');
            }
        }
        else
        {
            // Durum 2: Tırnaksız ama uzantıdan (.exe/.bat/.cmd/.lnk) sonra boşluk ve argümanlar var
            var match = Regex.Match(trimmed, @"^(.*?(\.exe|\.bat|\.cmd|\.lnk|\.ps1))\s+(.*)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                exe = match.Groups[1].Value.Trim('\"', ' ');
                string embeddedArgs = match.Groups[3].Value.Trim();
                if (!string.IsNullOrWhiteSpace(embeddedArgs))
                {
                    args = string.IsNullOrWhiteSpace(args) ? embeddedArgs : $"{embeddedArgs} {args}";
                }
            }
            else
            {
                exe = trimmed.Trim('\"', ' ');
            }
        }

        return (exe, args);
    }

    /// <summary>
    /// Komut satırı veya parametre içeren yoldan yalnızca çalıştırılabilir dosyanın yolunu ayıklar.
    /// </summary>
    public static string ExtractExecutablePath(string rawPath)
    {
        var (exe, _) = SplitCommandLine(rawPath);
        return exe;
    }

    /// <summary>
    /// Windows kısayol (.lnk) dosyasının hedef yolunu ve argümanlarını çözümler.
    /// </summary>
    public static (string targetPath, string arguments, string workingDir) ResolveShortcut(string lnkPath)
    {
        try
        {
            if (File.Exists(lnkPath) && lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic shortcut = shell.CreateShortcut(lnkPath);
                    string target = (string)shortcut.TargetPath;
                    string args = (string)shortcut.Arguments;
                    string workDir = (string)shortcut.WorkingDirectory;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        return (target, args ?? string.Empty, workDir ?? string.Empty);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ResolveShortcut error for '{lnkPath}': {ex.Message}");
        }

        return (lnkPath, string.Empty, string.Empty);
    }

    /// <summary>
    /// Uygulamayı çalıştırır veya çalışıyorsa penceresini öne getirir.
    /// Komut satırı argümanlarını ve çalışma dizinlerini eksiksiz destekler.
    /// </summary>
    public static bool LaunchOrFocus(string appPath, string? appArgs = null)
    {
        if (string.IsNullOrWhiteSpace(appPath))
            return false;

        try
        {
            // 1. Komut satırını program yolu ve argümanlara ayrıştır
            var (exePath, arguments) = SplitCommandLine(appPath, appArgs);

            // 2. Eğer kısayol (.lnk) ise hedef yolu ve parametrelerini çöz
            string workingDirectory = string.Empty;
            if (exePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = ResolveShortcut(exePath);
                if (File.Exists(resolved.targetPath))
                {
                    exePath = resolved.targetPath;
                    if (string.IsNullOrWhiteSpace(arguments) && !string.IsNullOrWhiteSpace(resolved.arguments))
                    {
                        arguments = resolved.arguments;
                    }
                    workingDirectory = resolved.workingDir;
                }
            }

            if (string.IsNullOrWhiteSpace(workingDirectory) && File.Exists(exePath))
            {
                workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            }

            // 3. Web URL kontrolü
            if (exePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                exePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
                return true;
            }

            // 4. Batch ve script dosyaları (.bat, .cmd, .ps1) doğrudan çalıştırılır
            string ext = Path.GetExtension(exePath).ToLowerInvariant();
            if (ext == ".bat" || ext == ".cmd" || ext == ".ps1")
            {
                var scriptStart = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory
                };
                Process.Start(scriptStart);
                return true;
            }

            // 5. Zaten çalışan penceresi var mı kontrol et (öne getir)
            string processName = Path.GetFileNameWithoutExtension(exePath);
            if (!string.IsNullOrWhiteSpace(processName))
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        if (IsIconic(proc.MainWindowHandle))
                        {
                            ShowWindowAsync(proc.MainWindowHandle, SW_RESTORE);
                        }
                        else
                        {
                            ShowWindowAsync(proc.MainWindowHandle, SW_SHOW);
                        }

                        SetForegroundWindow(proc.MainWindowHandle);
                        return true;
                    }
                }
            }

            // 5. Çalışmıyorsa belirtilen parametreler ve çalışma dizini ile sıfırdan başlat
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppLauncher error for '{appPath}': {ex.Message}");
            return false;
        }
    }
}
