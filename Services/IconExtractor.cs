using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace MacroDeck.Server.Services;

public static class IconExtractor
{
    public static byte[]? ExtractIconAsPngBytes(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        // Tırnakları veya parametreleri ayıkla
        string cleanPath = AppLauncher.ExtractExecutablePath(filePath);

        // Kısayol (.lnk) ise hedefini bul
        if (cleanPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = AppLauncher.ResolveShortcut(cleanPath);
            if (File.Exists(resolved.targetPath))
            {
                cleanPath = resolved.targetPath;
            }
        }

        if (!File.Exists(cleanPath))
            return null;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(cleanPath);
            if (icon == null) return null;

            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Icon extract error for '{cleanPath}': {ex.Message}");
            return null;
        }
    }
}
