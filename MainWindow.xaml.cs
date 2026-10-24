using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MacroDeck.Server.Models;
using MacroDeck.Server.Services;
using MacroDeck.Server.ViewModels;
using MacroDeck.Server.Views;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace MacroDeck.Server;

public partial class MainWindow : Window
{
    private readonly DeckConfig _config;
    private readonly List<DeckButtonViewModel> _buttonVMs = new();
    private DeckButtonViewModel? _selectedButton;

    private readonly AudioManager _audioManager;
    private readonly DeckServer _deckServer;
    private readonly TrayManager _trayManager;
    private readonly WeatherService _weatherService;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string _localIp = "127.0.0.1";
    private bool _isExplicitExit = false;
    private bool _isUpdatingVolumeFromEvent = false;
    private bool _isInitializingDimensions = true;
    private bool _isUpdatingActionType = false;

    public MainWindow()
    {
        InitializeComponent();

        _config = DeckConfig.Load();
        _config.Columns = Math.Clamp(_config.Columns, 3, 5);
        _config.Rows = Math.Clamp(_config.Rows, 3, 5);
        _config.EnsureGridSize();

        SliderColumns.Value = _config.Columns;
        SliderRows.Value = _config.Rows;
        TxtColumnsVal.Text = _config.Columns.ToString();
        TxtRowsVal.Text = _config.Rows.ToString();
        _isInitializingDimensions = false;

        TxtCityName.Text = string.IsNullOrWhiteSpace(_config.CityName) ? "İstanbul" : _config.CityName;

        _audioManager = new AudioManager();
        _deckServer = new DeckServer();
        _trayManager = new TrayManager(this);
        _weatherService = new WeatherService();
        _weatherService.WeatherUpdated += OnWeatherUpdated;
        _ = _weatherService.RefreshWeatherAsync(TxtCityName.Text);

        SetupTrayEvents();
        SetupAudioEvents();
        SetupServerEvents();
        SetupClockTimer();

        InitializeGrid();
        InitializeAudioSlider();
        StartServer();

        SelectButton(_buttonVMs.FirstOrDefault());
    }

    private void SetupClockTimer()
    {
        _clockTimer.Tick += (s, e) => UpdateClockDisplay();
        _clockTimer.Start();
        UpdateClockDisplay();
    }

    private void UpdateClockDisplay()
    {
        var now = DateTime.Now;
        // Gün-Ay formatı (Örn: 24 Ekim)
        TxtPreviewDate.Text = now.ToString("dd MMMM");
        // Dijital saat (Örn: 12:45)
        TxtPreviewTime.Text = now.ToString("HH:mm");
    }

    private void SetupTrayEvents()
    {
        _trayManager.OpenRequested += () => Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });

        _trayManager.QrRequested += () => Dispatcher.Invoke(ShowQrWindow);

        _trayManager.AutoStartChanged += (isEnabled) => Dispatcher.Invoke(() =>
        {
            ChkAutoStart.IsChecked = isEnabled;
        });

        _trayManager.ExitRequested += () => Dispatcher.Invoke(() =>
        {
            _isExplicitExit = true;
            Close();
        });
    }

    private void SetupAudioEvents()
    {
        // PC tarafında ses değiştiğinde (yerel anlık değişim)
        _audioManager.VolumeChangedImmediate += (vol) => Dispatcher.Invoke(() =>
        {
            if (!_isUpdatingVolumeFromEvent)
            {
                _isUpdatingVolumeFromEvent = true;
                SliderPreviewVolume.Value = vol;
                TxtPreviewVolumePercent.Text = $"%{vol}";
                _isUpdatingVolumeFromEvent = false;
            }
        });

        // 3 saniyelik gecikme sonrası telefona senkronizasyon
        _audioManager.DebouncedVolumeChanged += (vol) =>
        {
            _deckServer.BroadcastVolumeSync(vol);
        };
    }

    private void SetupServerEvents()
    {
        _deckServer.ConnectionStatusChanged += (isConnected) => Dispatcher.Invoke(() =>
        {
            UpdateConnectionBadge(isConnected);

            if (isConnected)
            {
                // Telefon yeni bağlandıysa mevcut konfigürasyonu, sesi ve hava durumunu otomatik yolla
                int currentVol = _audioManager.GetMasterVolume();
                _deckServer.BroadcastConfig(_config, currentVol);

                if (_weatherService.LatestData != null)
                {
                    _deckServer.BroadcastWeather(_weatherService.LatestData);
                }
            }
        });

        _deckServer.ButtonPressed += (buttonId) => Dispatcher.Invoke(() =>
        {
            var btn = _config.Buttons.FirstOrDefault(b => b.Id == buttonId);
            if (btn != null)
            {
                if (btn.ActionType == "weather")
                {
                    if (_weatherService.LatestData != null)
                    {
                        _deckServer.BroadcastWeather(_weatherService.LatestData);
                    }
                }
                else if (btn.ActionType == "market")
                {
                    var items = (btn.MarketItems != null && btn.MarketItems.Count > 0)
                        ? btn.MarketItems
                        : _config.MarketItems;
                    _deckServer.BroadcastMarketConfig(items);
                }
                else if (btn.ActionType == "system")
                {
                    SystemMonitorService.Instance.Start(btn.SystemInterval);
                }
                else if (btn.ActionType == "process")
                {
                    ProcessMonitorService.Instance.SetTargetProcess(0, btn.TargetProcess);
                    ProcessMonitorService.Instance.Start();
                }
                else if (!string.IsNullOrWhiteSpace(btn.AppPath))
                {
                    AppLauncher.LaunchOrFocus(btn.AppPath, btn.AppArgs);
                }
            }
        });

        _deckServer.VolumeSetRequested += (vol) => Dispatcher.Invoke(() =>
        {
            _audioManager.SetMasterVolume(vol, fromRemote: true);
        });

        _deckServer.SystemMonitorRequested += (start, interval) =>
        {
            if (start)
            {
                SystemMonitorService.Instance.Start(interval);
            }
            else
            {
                SystemMonitorService.Instance.Stop();
            }
        };

        SystemMonitorService.Instance.StatsUpdated += (stats) =>
        {
            _deckServer.BroadcastSystemStats(stats);
        };

        _deckServer.ProcessMonitorRequested += (start, targetProc) =>
        {
            if (start)
            {
                if (!string.IsNullOrWhiteSpace(targetProc))
                {
                    ProcessMonitorService.Instance.SetTargetProcess(0, targetProc);
                }
                ProcessMonitorService.Instance.Start();
            }
            else
            {
                ProcessMonitorService.Instance.Stop();
            }
        };

        _deckServer.ProcessSelectionRequested += (pid) =>
        {
            ProcessMonitorService.Instance.SetTargetPid(pid);
        };


        _deckServer.ProcessListRequested += () =>
        {
            var list = ProcessMonitorService.Instance.GetRunningProcesses();
            _deckServer.BroadcastProcessList(list);
        };

        ProcessMonitorService.Instance.StatsUpdated += (stats) =>
        {
            _deckServer.BroadcastProcessStats(stats);
        };
    }

    private void StartServer()
    {
        _localIp = NetworkHelper.GetLocalIpAddress();
        _deckServer.Start();
        UpdateConnectionBadge(false);
    }

    private void UpdateConnectionBadge(bool isConnected)
    {
        if (isConnected)
        {
            BadgeConnection.Background = new SolidColorBrush(Color.FromRgb(20, 83, 45)); // Yeşil
            DotConnection.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            TxtConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            TxtConnectionStatus.Text = $"● Telefon Bağlı ({_localIp}:{_deckServer.Port})";
        }
        else
        {
            BadgeConnection.Background = new SolidColorBrush(Color.FromRgb(59, 38, 18)); // Sarı/Kahve
            DotConnection.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            TxtConnectionStatus.Text = $"○ Telefon Bekleniyor ({_localIp}:{_deckServer.Port})";
        }
    }

    private void InitializeGrid()
    {
        _buttonVMs.Clear();
        PreviewUniformGrid.Children.Clear();
        PreviewUniformGrid.Columns = _config.Columns;
        PreviewUniformGrid.Rows = _config.Rows;

        if (TxtPreviewHeader != null)
        {
            TxtPreviewHeader.Text = $"📱 MONİTÖR ALTI TELEFON CANLI ÖN İZLEME ({_config.Columns}x{_config.Rows})";
        }

        for (int r = 0; r < _config.Rows; r++)
        {
            for (int c = 0; c < _config.Columns; c++)
            {
                var model = _config.GetButton(r, c) ?? new DeckButton { Row = r, Col = c, Id = $"btn_{r}_{c}" };
                var vm = DeckButtonViewModel.FromModel(model);
                _buttonVMs.Add(vm);

                var cellBorder = CreatePreviewCell(vm);
                PreviewUniformGrid.Children.Add(cellBorder);
            }
        }
    }

    private Border CreatePreviewCell(DeckButtonViewModel vm)
    {
        var border = new Border
        {
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            Background = vm.BackgroundBrush,
            Cursor = Cursors.Hand,
            Tag = vm
        };

        var grid = new Grid { Margin = new Thickness(4) };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var img = new Image
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(0, 0, 0, 4),
            Source = vm.IconImage,
            Visibility = vm.HasIcon ? Visibility.Visible : Visibility.Collapsed
        };

        var txt = new TextBlock
        {
            Text = vm.DisplayTitle,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 100
        };

        stack.Children.Add(img);
        stack.Children.Add(txt);
        grid.Children.Add(stack);
        border.Child = grid;

        // ViewModel değiştiğinde ön izleme hücresini anlık güncelle
        vm.PropertyChanged += (s, e) =>
        {
            border.Background = vm.BackgroundBrush;
            txt.Text = vm.DisplayTitle;
            img.Source = vm.IconImage;
            img.Visibility = vm.HasIcon ? Visibility.Visible : Visibility.Collapsed;

            if (vm.IsSelected)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Seçili neon mavi
            }
            else
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            }
        };

        border.MouseDown += (s, e) =>
        {
            SelectButton(vm);
        };

        return border;
    }

    private void SelectButton(DeckButtonViewModel? vm)
    {
        if (vm == null) return;

        foreach (var b in _buttonVMs)
        {
            b.IsSelected = (b == vm);
        }

        _selectedButton = vm;

        TxtSelectedCellInfo.Text = $"Seçili Hücre: [Satır {vm.Row + 1}, Sütun {vm.Col + 1}]";
        TxtButtonTitle.Text = vm.Title;
        TxtAppPath.Text = vm.AppPath;
        TxtAppArgs.Text = vm.AppArgs;
        TxtIconPath.Text = vm.IconPath ?? string.Empty;

        _isUpdatingActionType = true;
        if (CmbActionType != null)
        {
            if (vm.IsProcess) CmbActionType.SelectedIndex = 4;
            else if (vm.IsSystem) CmbActionType.SelectedIndex = 3;
            else if (vm.IsMarket) CmbActionType.SelectedIndex = 2;
            else if (vm.IsWeather) CmbActionType.SelectedIndex = 1;
            else CmbActionType.SelectedIndex = 0;
        }
        if (TxtProcessTarget != null)
        {
            TxtProcessTarget.Text = vm.TargetProcess ?? string.Empty;
        }
        if (SliderSystemInterval != null)
        {
            SliderSystemInterval.Value = vm.SystemInterval > 0 ? vm.SystemInterval : 1.0;
        }
        if (TxtSystemIntervalVal != null)
        {
            TxtSystemIntervalVal.Text = $"{vm.SystemInterval:0.0} sn";
        }
        if (PanelAppPath != null) PanelAppPath.Visibility = (vm.IsWeather || vm.IsMarket || vm.IsSystem || vm.IsProcess) ? Visibility.Collapsed : Visibility.Visible;
        if (PanelWeatherInfo != null) PanelWeatherInfo.Visibility = vm.IsWeather ? Visibility.Visible : Visibility.Collapsed;
        if (PanelMarketInfo != null) PanelMarketInfo.Visibility = vm.IsMarket ? Visibility.Visible : Visibility.Collapsed;
        if (PanelSystemInfo != null) PanelSystemInfo.Visibility = vm.IsSystem ? Visibility.Visible : Visibility.Collapsed;
        if (PanelProcessInfo != null) PanelProcessInfo.Visibility = vm.IsProcess ? Visibility.Visible : Visibility.Collapsed;
        UpdateCellMarketSummary();
        _isUpdatingActionType = false;
    }

    private void UpdateCellMarketSummary()
    {
        if (_selectedButton == null || TxtCellMarketSummary == null) return;

        if (_selectedButton.MarketItems != null && _selectedButton.MarketItems.Count > 0)
        {
            var names = string.Join(", ", _selectedButton.MarketItems.Select(m => m.Name));
            TxtCellMarketSummary.Text = $"Özel: {names} ({_selectedButton.MarketItems.Count} Adet)";
            TxtCellMarketSummary.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Neon Cyan
        }
        else
        {
            TxtCellMarketSummary.Text = $"Varsayılan Liste ({_config.MarketItems.Count} Varlık)";
            TxtCellMarketSummary.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Gri
        }
    }

    private void BtnEditCellMarket_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedButton == null) return;

        var baseItems = (_selectedButton.MarketItems != null && _selectedButton.MarketItems.Count > 0)
            ? _selectedButton.MarketItems
            : _config.MarketItems;

        var win = new MarketSettingsWindow(baseItems)
        {
            Owner = this,
            Title = $"Hücre [{_selectedButton.Row + 1}, {_selectedButton.Col + 1}] Piyasa Ayarları"
        };

        if (win.ShowDialog() == true)
        {
            _selectedButton.MarketItems = win.ResultItems;
            UpdateCellMarketSummary();
            SyncViewModelsToConfig();
            _config.Save();

            // Telefona güncel hücre tasarımını ve piyasa ayarlarını anında aktar
            _deckServer.BroadcastMarketConfig(_selectedButton.MarketItems);
            _deckServer.BroadcastConfig(_config, _audioManager.GetMasterVolume());

            CardStatusNotification.Background = new SolidColorBrush(Color.FromRgb(20, 83, 45));
            CardStatusNotification.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            TxtStatusNotification.Foreground = new SolidColorBrush(Color.FromRgb(220, 252, 231));
            TxtStatusNotification.Text = $"✅ Hücre [{_selectedButton.Row + 1}, {_selectedButton.Col + 1}] için {_selectedButton.MarketItems.Count} piyasa varlığı kaydedildi!";
        }
    }

    private void CmbActionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingActionType || _selectedButton == null) return;

        string act = CmbActionType.SelectedIndex switch
        {
            4 => "process",
            3 => "system",
            2 => "market",
            1 => "weather",
            _ => "app"
        };
        _selectedButton.ActionType = act;

        bool isApp = act == "app";
        bool isWeather = act == "weather";
        bool isMarket = act == "market";
        bool isSystem = act == "system";
        bool isProcess = act == "process";

        if (PanelAppPath != null) PanelAppPath.Visibility = isApp ? Visibility.Visible : Visibility.Collapsed;
        if (PanelWeatherInfo != null) PanelWeatherInfo.Visibility = isWeather ? Visibility.Visible : Visibility.Collapsed;
        if (PanelMarketInfo != null) PanelMarketInfo.Visibility = isMarket ? Visibility.Visible : Visibility.Collapsed;
        if (PanelSystemInfo != null) PanelSystemInfo.Visibility = isSystem ? Visibility.Visible : Visibility.Collapsed;
        if (PanelProcessInfo != null) PanelProcessInfo.Visibility = isProcess ? Visibility.Visible : Visibility.Collapsed;
        UpdateCellMarketSummary();

        if (isWeather && string.IsNullOrWhiteSpace(_selectedButton.Title))
        {
            _selectedButton.Title = "Hava";
            TxtButtonTitle.Text = "Hava";
        }
        else if (isMarket && string.IsNullOrWhiteSpace(_selectedButton.Title))
        {
            _selectedButton.Title = "Piyasa";
            TxtButtonTitle.Text = "Piyasa";
        }
        else if (isSystem && string.IsNullOrWhiteSpace(_selectedButton.Title))
        {
            _selectedButton.Title = "Sistem";
            TxtButtonTitle.Text = "Sistem";
        }
        else if (isProcess && string.IsNullOrWhiteSpace(_selectedButton.Title))
        {
            _selectedButton.Title = "Takip";
            TxtButtonTitle.Text = "Takip";
        }
    }


    private void SliderSystemInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSystemIntervalVal != null)
        {
            TxtSystemIntervalVal.Text = $"{e.NewValue:0.0} sn";
        }
        if (_selectedButton != null)
        {
            _selectedButton.SystemInterval = e.NewValue;
        }
    }

    private async void BtnRefreshWeather_Click(object sender, RoutedEventArgs e)
    {
        string city = TxtCityName.Text.Trim();
        if (!string.IsNullOrWhiteSpace(city))
        {
            _config.CityName = city;
            _config.Save();
            await _weatherService.RefreshWeatherAsync(city);
        }
    }

    private void OnWeatherUpdated(WeatherData data)
    {
        Dispatcher.Invoke(() =>
        {
            _deckServer.BroadcastWeather(data);
        });
    }

    private void InitializeAudioSlider()
    {
        int vol = _audioManager.GetMasterVolume();
        SliderPreviewVolume.Value = vol;
        TxtPreviewVolumePercent.Text = $"%{vol}";
    }

    private void SliderPreviewVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeFromEvent) return;

        int vol = (int)Math.Round(e.NewValue);
        if (TxtPreviewVolumePercent != null)
        {
            TxtPreviewVolumePercent.Text = $"%{vol}";
        }
        _audioManager?.SetMasterVolume(vol);
    }

    private void TxtButtonTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedButton != null)
        {
            _selectedButton.Title = TxtButtonTitle.Text;
        }
    }

    private bool _isSplittingCommand;

    private void TxtAppPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSplittingCommand || _selectedButton == null) return;

        string input = TxtAppPath.Text;
        // Kullanıcı doğrudan tırnaklı komut veya argümanlı yol yapıştırdıysa otomatik ayıkla
        // Örn: "C:\Program Files\SteelSeries\GG\SteelSeriesGGEZ.exe" -dataPath="C:\ProgramData\SteelSeries\GG" -dbEnv=production
        if (!string.IsNullOrWhiteSpace(input) && (input.StartsWith("\"") || input.Contains(".exe ", StringComparison.OrdinalIgnoreCase)))
        {
            var (exe, args) = AppLauncher.SplitCommandLine(input);
            if (!string.IsNullOrWhiteSpace(args))
            {
                _isSplittingCommand = true;
                TxtAppPath.Text = exe;
                TxtAppArgs.Text = args;
                _selectedButton.AppPath = exe;
                _selectedButton.AppArgs = args;

                if (string.IsNullOrWhiteSpace(_selectedButton.Title))
                {
                    string procName = Path.GetFileNameWithoutExtension(exe);
                    TxtButtonTitle.Text = procName;
                    _selectedButton.Title = procName;
                }

                byte[]? iconBytes = IconExtractor.ExtractIconAsPngBytes(exe);
                if (iconBytes != null && iconBytes.Length > 0)
                {
                    _selectedButton.SetIconFromBytes(iconBytes, exe);
                    TxtIconPath.Text = $"[Simge] {Path.GetFileName(exe)}";
                }

                _isSplittingCommand = false;
                return;
            }
        }

        _selectedButton.AppPath = TxtAppPath.Text;
    }

    private void TxtAppArgs_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSplittingCommand || _selectedButton == null) return;
        _selectedButton.AppArgs = TxtAppArgs.Text;
    }

    private void BtnBrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Hedef Uygulama veya Betik (.exe, .bat, .cmd, .lnk) Seçin",
            Filter = "Tüm Çalıştırılabilir Dosyalar (*.exe;*.bat;*.cmd;*.lnk;*.ps1)|*.exe;*.bat;*.cmd;*.lnk;*.ps1|Toplu İş Dosyaları (*.bat;*.cmd)|*.bat;*.cmd|Uygulamalar (*.exe)|*.exe|Kısayollar (*.lnk)|*.lnk|Tüm Dosyalar (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true && _selectedButton != null)
        {
            string chosenFile = ofd.FileName;
            string targetPath = chosenFile;
            string targetArgs = string.Empty;

            // Kısayol (.lnk) seçildiyse hedefi ve özel parametreleri çözümle
            if (chosenFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = AppLauncher.ResolveShortcut(chosenFile);
                if (!string.IsNullOrWhiteSpace(resolved.targetPath) && File.Exists(resolved.targetPath))
                {
                    targetPath = resolved.targetPath;
                    targetArgs = resolved.arguments;
                }
            }

            _isSplittingCommand = true;
            TxtAppPath.Text = targetPath;
            _selectedButton.AppPath = targetPath;

            TxtAppArgs.Text = targetArgs;
            _selectedButton.AppArgs = targetArgs;
            _isSplittingCommand = false;

            // 1. Başlık boşsa program adını otomatik doldur
            if (string.IsNullOrWhiteSpace(_selectedButton.Title))
            {
                string procName = Path.GetFileNameWithoutExtension(targetPath);
                TxtButtonTitle.Text = procName;
                _selectedButton.Title = procName;
            }

            // 2. Uygulamanın simgesini (Icon) otomatik çıkar ve butona aktar
            byte[]? iconBytes = IconExtractor.ExtractIconAsPngBytes(targetPath) ?? IconExtractor.ExtractIconAsPngBytes(chosenFile);
            if (iconBytes != null && iconBytes.Length > 0)
            {
                _selectedButton.SetIconFromBytes(iconBytes, targetPath);
                TxtIconPath.Text = $"[Simge] {Path.GetFileName(targetPath)}";
            }
        }
    }

    private void TxtProcessTarget_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedButton != null && TxtProcessTarget != null)
        {
            _selectedButton.TargetProcess = TxtProcessTarget.Text.Trim();
        }
    }

    private void CmbRunningProcesses_DropDownOpened(object sender, EventArgs e)
    {
        try
        {
            var processes = ProcessMonitorService.Instance.GetRunningProcesses();
            CmbRunningProcesses.Items.Clear();
            CmbRunningProcesses.Items.Add(new ComboBoxItem { Content = "-- Açık İşlem Seçin --", Tag = "" });
            foreach (var p in processes)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{p.Name} ({p.RamMb:0} MB)",
                    Tag = p.Name,
                    ToolTip = $"{p.Title} (PID: {p.Pid})"
                };
                CmbRunningProcesses.Items.Add(item);
            }
        }
        catch { }
    }

    private void CmbRunningProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbRunningProcesses.SelectedItem is ComboBoxItem item && item.Tag is string exe && !string.IsNullOrWhiteSpace(exe))
        {
            TxtProcessTarget.Text = exe;
            if (_selectedButton != null)
            {
                _selectedButton.TargetProcess = exe;
                if (string.IsNullOrWhiteSpace(_selectedButton.Title) || _selectedButton.Title == "Takip")
                {
                    string shortTitle = Path.GetFileNameWithoutExtension(exe);
                    _selectedButton.Title = shortTitle;
                    TxtButtonTitle.Text = shortTitle;
                }
            }
        }
    }

    private void BtnBrowseProcessExe_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Takip Edilecek Uygulama Dosyasını (.exe) Seçin",
            Filter = "Uygulama Dosyaları (*.exe)|*.exe|Tüm Dosyalar (*.*)|*.*"
        };
        if (ofd.ShowDialog() == true)
        {
            string fileName = Path.GetFileName(ofd.FileName);
            TxtProcessTarget.Text = fileName;
            if (_selectedButton != null)
            {
                _selectedButton.TargetProcess = fileName;
                if (string.IsNullOrWhiteSpace(_selectedButton.Title) || _selectedButton.Title == "Takip")
                {
                    string shortTitle = Path.GetFileNameWithoutExtension(fileName);
                    _selectedButton.Title = shortTitle;
                    TxtButtonTitle.Text = shortTitle;
                }
            }
        }
    }

    private void BtnBrowseIcon_Click(object sender, RoutedEventArgs e)

    {
        var ofd = new OpenFileDialog
        {
            Title = "Buton İkonu / Resmi Seçin",
            Filter = "Görseller (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico|Tüm Dosyalar (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true && _selectedButton != null)
        {
            TxtIconPath.Text = ofd.FileName;
            _selectedButton.IconPath = ofd.FileName;
        }
    }

    private void BtnClearIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedButton != null)
        {
            TxtIconPath.Text = string.Empty;
            _selectedButton.IconPath = null;
        }
    }

    private void BtnResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedButton != null)
        {
            _selectedButton.Reset();
            _selectedButton.MarketItems = null;
            TxtAppArgs.Text = string.Empty;
            UpdateCellMarketSummary();
            SelectButton(_selectedButton);
        }
    }

    private void SliderGridDimensions_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializingDimensions) return;
        if (SliderColumns == null || SliderRows == null) return;

        int newCols = (int)Math.Round(SliderColumns.Value);
        int newRows = (int)Math.Round(SliderRows.Value);

        if (TxtColumnsVal != null) TxtColumnsVal.Text = newCols.ToString();
        if (TxtRowsVal != null) TxtRowsVal.Text = newRows.ToString();

        if (_config.Columns != newCols || _config.Rows != newRows)
        {
            // Mevcut ViewModel'lerdeki değişiklikleri config'e aktar
            SyncViewModelsToConfig();

            _config.Columns = newCols;
            _config.Rows = newRows;
            _config.EnsureGridSize();

            InitializeGrid();

            // Seçili buton yeni sınırlar dışındaysa ilk geçerli butonu seç
            if (_selectedButton == null || _selectedButton.Row >= newRows || _selectedButton.Col >= newCols)
            {
                SelectButton(_buttonVMs.FirstOrDefault());
            }
        }
    }

    private void SyncViewModelsToConfig()
    {
        foreach (var vm in _buttonVMs)
        {
            var existing = _config.GetButton(vm.Row, vm.Col);
            if (existing != null)
            {
                existing.Title = vm.Title;
                existing.AppPath = vm.AppPath;
                existing.AppArgs = vm.AppArgs;
                existing.ActionType = vm.ActionType;
                existing.BackgroundColor = vm.BackgroundColor;
                existing.IconPath = vm.IconPath;
                existing.IconBase64 = vm.IconBase64;
                existing.MarketItems = vm.MarketItems != null ? new List<MarketItemConfig>(vm.MarketItems) : null;
                existing.SystemInterval = vm.SystemInterval;
                existing.TargetProcess = vm.TargetProcess;
            }
            else
            {
                _config.Buttons.Add(vm.ToModel());
            }

        }
    }

    private void BtnSaveAndSync_Click(object sender, RoutedEventArgs e)
    {
        // 1. Modelleri güncelle ve diske kaydet
        SyncViewModelsToConfig();
        _config.Save();

        // 2. Telefon bağlantısını kontrol et
        int currentVol = _audioManager.GetMasterVolume();
        bool sent = _deckServer.BroadcastConfig(_config, currentVol);

        if (sent)
        {
            // Telefon bağlı ve anında senkronize edildi
            CardStatusNotification.Background = new SolidColorBrush(Color.FromRgb(20, 83, 45)); // Yeşil
            CardStatusNotification.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            TxtStatusNotification.Foreground = new SolidColorBrush(Color.FromRgb(220, 252, 231));
            TxtStatusNotification.Text = "✅ Tasarım başarıyla kaydedildi ve telefona anında senkronize edildi!";
        }
        else
        {
            // Telefon bağlı değil / uygulama kapalı -> Kullanıcıya uyarı ver
            CardStatusNotification.Background = new SolidColorBrush(Color.FromRgb(69, 26, 3)); // Koyu Amber
            CardStatusNotification.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtStatusNotification.Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199));
            TxtStatusNotification.Text = "⚠️ Tasarım bilgisayara kaydedildi ancak telefonda uygulama çalışmıyor veya senkronizasyon sağlanamadı. Lütfen telefondan uygulamayı açın (Telefon açıldığında tasarım otomatik yüklenecektir).";

            _trayManager.ShowNotification(
                "Senkronizasyon Uyarısı",
                "Tasarım kaydedildi ancak telefona aktarılamadı. Lütfen telefondaki MacroDeck uygulamasını açın.",
                System.Windows.Forms.ToolTipIcon.Warning
            );
        }
    }

    private void BtnMarketSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new MarketSettingsWindow(_config.MarketItems)
        {
            Owner = this,
            Title = "Genel Piyasa Takip Ayarları (Varsayılan)"
        };

        if (win.ShowDialog() == true)
        {
            _config.MarketItems = win.ResultItems;
            _config.Save();

            _deckServer.BroadcastMarketConfig(_config.MarketItems);
            UpdateCellMarketSummary();

            CardStatusNotification.Background = new SolidColorBrush(Color.FromRgb(20, 83, 45));
            CardStatusNotification.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            TxtStatusNotification.Foreground = new SolidColorBrush(Color.FromRgb(220, 252, 231));
            TxtStatusNotification.Text = $"✅ Takip edilecek {_config.MarketItems.Count} varsayılan varlık kaydedildi ve telefona bildirildi!";
        }
    }

    private void BtnShowQr_Click(object sender, RoutedEventArgs e)
    {
        ShowQrWindow();
    }

    private void ShowQrWindow()
    {
        var qrWin = new QrWindow(_localIp, _deckServer.Port)
        {
            Owner = this
        };
        qrWin.ShowDialog();
    }

    private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        _trayManager.MinimizeToTray();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        // Görev çubuğuna küçültülmesini engelleyip doğrudan sistem tepsisine yönlendir
        if (WindowState == WindowState.Minimized)
        {
            _trayManager.MinimizeToTray();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Eğer kullanıcı sağ tık menüsünden "Çıkış" demediyse, çarpı butonuna basıldığında tepsiye küçült
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            _trayManager.MinimizeToTray();
        }
        else
        {
            _audioManager.Dispose();
            _deckServer.Dispose();
            _trayManager.Dispose();
            _weatherService.Dispose();
            _clockTimer.Stop();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Windows başlangıcında çalışma durumunu kontrol et
        ChkAutoStart.IsChecked = StartupManager.IsAutoStartEnabled();

        // Eğer başlangıçta --minimized ile açıldıysa doğrudan sistem tepsisine küçült
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) || a.Equals("/minimized", StringComparison.OrdinalIgnoreCase)))
        {
            _trayManager.MinimizeToTray();
        }
    }

    private void ChkAutoStart_Checked(object sender, RoutedEventArgs e)
    {
        StartupManager.SetAutoStart(true);
        _trayManager.UpdateAutoStartCheck(true);
    }

    private void ChkAutoStart_Unchecked(object sender, RoutedEventArgs e)
    {
        StartupManager.SetAutoStart(false);
        _trayManager.UpdateAutoStartCheck(false);
    }
}