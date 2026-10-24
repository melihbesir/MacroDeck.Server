using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace MacroDeck.Server.Services;

public class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Window _mainWindow;

    private ToolStripMenuItem? _autoStartMenuItem;

    public event Action? OpenRequested;
    public event Action? QrRequested;
    public event Action<bool>? AutoStartChanged;
    public event Action? ExitRequested;

    public TrayManager(Window mainWindow)
    {
        _mainWindow = mainWindow;

        System.Drawing.Icon trayIcon = System.Drawing.SystemIcons.Application;
        try
        {
            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app_icon.ico");
            if (System.IO.File.Exists(icoPath))
            {
                trayIcon = new System.Drawing.Icon(icoPath);
            }
        }
        catch { }

        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "MacroDeck - Mobil Kontrol Paneli",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Göster", null, (s, e) => OpenWindow());
        contextMenu.Items.Add("Eşleştirme QR Kodu", null, (s, e) => QrRequested?.Invoke());
        contextMenu.Items.Add("-");

        _autoStartMenuItem = new ToolStripMenuItem("Windows ile Başlat")
        {
            Checked = StartupManager.IsAutoStartEnabled(),
            CheckOnClick = true
        };
        _autoStartMenuItem.Click += (s, e) =>
        {
            bool newState = _autoStartMenuItem.Checked;
            StartupManager.SetAutoStart(newState);
            AutoStartChanged?.Invoke(newState);
        };
        contextMenu.Items.Add(_autoStartMenuItem);

        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Çıkış", null, (s, e) => ExitRequested?.Invoke());

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => OpenWindow();
    }

    public void UpdateAutoStartCheck(bool isEnabled)
    {
        if (_autoStartMenuItem != null)
        {
            _autoStartMenuItem.Checked = isEnabled;
        }
    }

    public void MinimizeToTray()
    {
        _mainWindow.Hide();
        _notifyIcon.ShowBalloonTip(
            2000,
            "MacroDeck Arka Planda",
            "Uygulama bildirim alanına küçültüldü. Çift tıklayarak tekrar açabilirsiniz.",
            ToolTipIcon.Info
        );
    }

    public void OpenWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        OpenRequested?.Invoke();
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
