using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroDeck.Server.Models;
using MacroDeck.Server.Services;

namespace MacroDeck.Server.ViewModels;

public class DeckButtonViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _appPath = string.Empty;
    private string _appArgs = string.Empty;
    private string _backgroundColor = "#1e222d";
    private string? _iconPath;
    private string? _iconBase64;
    private BitmapImage? _iconImage;
    private bool _isSelected;

    private string _actionType = "app";
    private string _targetProcess = string.Empty;
    private List<MarketItemConfig>? _marketItems;
    private double _systemInterval = 1.0;
    public string Id { get; set; } = string.Empty;
    public int Row { get; set; }
    public int Col { get; set; }

    public string DisplayPosition => $"[{Row + 1}, {Col + 1}]";

    public string TargetProcess
    {
        get => _targetProcess;
        set => SetField(ref _targetProcess, value);
    }

    public List<MarketItemConfig>? MarketItems
    {
        get => _marketItems;
        set => SetField(ref _marketItems, value);
    }

    public double SystemInterval
    {
        get => _systemInterval;
        set => SetField(ref _systemInterval, value);
    }

    public string ActionType
    {
        get => _actionType;
        set
        {
            if (SetField(ref _actionType, value))
            {
                OnPropertyChanged(nameof(IsWeather));
                OnPropertyChanged(nameof(IsMarket));
                OnPropertyChanged(nameof(IsSystem));
                OnPropertyChanged(nameof(IsProcess));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public bool IsWeather => _actionType == "weather";
    public bool IsMarket => _actionType == "market";
    public bool IsSystem => _actionType == "system";
    public bool IsProcess => _actionType == "process";

    public string Title
    {
        get => _title;
        set { SetField(ref _title, value); OnPropertyChanged(nameof(DisplayTitle)); }
    }

    public string DisplayTitle
    {
        get
        {
            if (IsWeather)
            {
                return string.IsNullOrWhiteSpace(_title) ? "⛅ Hava" : _title;
            }
            if (IsMarket)
            {
                return string.IsNullOrWhiteSpace(_title) ? "📈 Piyasa" : _title;
            }
            if (IsSystem)
            {
                return string.IsNullOrWhiteSpace(_title) ? "🖥️ Sistem" : _title;
            }
            if (IsProcess)
            {
                return string.IsNullOrWhiteSpace(_title) ? (!string.IsNullOrWhiteSpace(_targetProcess) ? $"🎯 {_targetProcess}" : "🎯 Uygulama") : _title;
            }
            return string.IsNullOrWhiteSpace(_title) ? $"+ ({Row + 1},{Col + 1})" : _title;
        }
    }

    public string AppPath
    {
        get => _appPath;
        set => SetField(ref _appPath, value);
    }

    public string AppArgs
    {
        get => _appArgs;
        set => SetField(ref _appArgs, value);
    }

    public string BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (SetField(ref _backgroundColor, value))
            {
                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }
    }

    public System.Windows.Media.Brush BackgroundBrush
    {
        get
        {
            try
            {
                return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_backgroundColor)!;
            }
            catch
            {
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 34, 45));
            }
        }
    }

    public string? IconPath
    {
        get => _iconPath;
        set
        {
            if (SetField(ref _iconPath, value))
            {
                LoadIconImage();
            }
        }
    }

    public string? IconBase64
    {
        get => _iconBase64;
        set => SetField(ref _iconBase64, value);
    }

    public BitmapImage? IconImage
    {
        get => _iconImage;
        private set
        {
            SetField(ref _iconImage, value);
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _iconImage != null;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public void LoadIconImage()
    {
        if (string.IsNullOrWhiteSpace(_iconPath))
        {
            IconImage = null;
            IconBase64 = null;
            return;
        }

        string cleanPath = AppLauncher.ExtractExecutablePath(_iconPath);
        if (cleanPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = AppLauncher.ResolveShortcut(cleanPath);
            if (File.Exists(resolved.targetPath))
            {
                cleanPath = resolved.targetPath;
            }
        }

        if (!File.Exists(cleanPath))
        {
            IconImage = null;
            IconBase64 = null;
            return;
        }

        try
        {
            byte[]? bytes;
            string ext = Path.GetExtension(cleanPath).ToLowerInvariant();
            if (ext == ".exe" || ext == ".lnk" || ext == ".bat" || ext == ".cmd" || ext == ".ps1")
            {
                bytes = IconExtractor.ExtractIconAsPngBytes(cleanPath);
            }
            else
            {
                bytes = File.ReadAllBytes(cleanPath);
            }

            if (bytes != null && bytes.Length > 0)
            {
                _iconBase64 = Convert.ToBase64String(bytes);
                OnPropertyChanged(nameof(IconBase64));

                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                IconImage = bmp;
            }
            else
            {
                IconImage = null;
                IconBase64 = null;
            }
        }
        catch
        {
            IconImage = null;
            IconBase64 = null;
        }
    }

    public void SetIconFromBytes(byte[] bytes, string? sourcePath = null)
    {
        try
        {
            _iconBase64 = Convert.ToBase64String(bytes);
            _iconPath = sourcePath;
            OnPropertyChanged(nameof(IconBase64));
            OnPropertyChanged(nameof(IconPath));

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            IconImage = bmp;
        }
        catch
        {
            IconImage = null;
            IconBase64 = null;
        }
    }

    public void Reset()
    {
        Title = string.Empty;
        AppPath = string.Empty;
        AppArgs = string.Empty;
        BackgroundColor = "#1e222d";
        _iconPath = null;
        _iconBase64 = null;
        _actionType = "app";
        _targetProcess = string.Empty;
        _marketItems = null;
        _systemInterval = 1.0;
        OnPropertyChanged(nameof(IconPath));
        OnPropertyChanged(nameof(IconBase64));
        OnPropertyChanged(nameof(MarketItems));
        OnPropertyChanged(nameof(SystemInterval));
        OnPropertyChanged(nameof(TargetProcess));
        IconImage = null;
    }

    public DeckButton ToModel()
    {
        return new DeckButton
        {
            Id = Id,
            Row = Row,
            Col = Col,
            Title = Title,
            AppPath = AppPath,
            AppArgs = AppArgs,
            ActionType = ActionType,
            TargetProcess = TargetProcess,
            BackgroundColor = BackgroundColor,
            IconPath = IconPath,
            IconBase64 = IconBase64,
            MarketItems = MarketItems != null ? new List<MarketItemConfig>(MarketItems) : null,
            SystemInterval = SystemInterval > 0 ? SystemInterval : 1.0
        };
    }

    public static DeckButtonViewModel FromModel(DeckButton model)
    {
        var vm = new DeckButtonViewModel
        {
            Id = model.Id,
            Row = model.Row,
            Col = model.Col,
            Title = model.Title,
            AppPath = model.AppPath,
            AppArgs = model.AppArgs ?? string.Empty,
            ActionType = string.IsNullOrWhiteSpace(model.ActionType) ? "app" : model.ActionType,
            TargetProcess = model.TargetProcess ?? string.Empty,
            BackgroundColor = string.IsNullOrWhiteSpace(model.BackgroundColor) ? "#1e222d" : model.BackgroundColor,
            _iconPath = model.IconPath,
            _iconBase64 = model.IconBase64,
            _systemInterval = model.SystemInterval > 0 ? model.SystemInterval : 1.0,
            MarketItems = model.MarketItems != null ? new List<MarketItemConfig>(model.MarketItems) : null
        };

        if (!string.IsNullOrWhiteSpace(model.IconBase64))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(model.IconBase64);
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                vm.IconImage = bmp;
            }
            catch { }
        }
        else if (!string.IsNullOrWhiteSpace(model.IconPath))
        {
            vm.LoadIconImage();
        }
        else if (!string.IsNullOrWhiteSpace(model.AppPath))
        {
            string cleanApp = AppLauncher.ExtractExecutablePath(model.AppPath);
            if (cleanApp.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = AppLauncher.ResolveShortcut(cleanApp);
                if (File.Exists(resolved.targetPath)) cleanApp = resolved.targetPath;
            }
            if (File.Exists(cleanApp))
            {
                vm._iconPath = cleanApp;
                vm.LoadIconImage();
            }
        }

        return vm;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
