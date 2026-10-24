using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroDeck.Server.Models;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace MacroDeck.Server.Views;

public class MarketItemDisplay
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Category { get; set; } = "crypto";
    public string ColorHex { get; set; } = "#38BDF8";
    public string DisplayName => $"{Name} ({Symbol})";

    public Brush ColorBrush
    {
        get
        {
            try
            {
                return (Brush)new BrushConverter().ConvertFromString(ColorHex)!;
            }
            catch
            {
                return Brushes.SkyBlue;
            }
        }
    }
}

public partial class MarketSettingsWindow : Window
{
    private static readonly string[] SlotColors = new[]
    {
        "#38BDF8", // Neon Cyan
        "#10B981", // Emerald Green
        "#F59E0B", // Amber Yellow
        "#F43F5E", // Rose Pink
        "#A855F7", // Purple
        "#3B82F6", // Royal Blue
        "#EC4899", // Fuchsia
        "#14B8A6", // Teal
        "#EAB308", // Golden
        "#6366F1"  // Indigo
    };

    private readonly ObservableCollection<MarketItemDisplay> _selectedItems = new();
    public List<MarketItemConfig> ResultItems { get; private set; } = new();

    public MarketSettingsWindow(List<MarketItemConfig> currentItems)
    {
        InitializeComponent();
        ItemsSelected.ItemsSource = _selectedItems;

        // Mevcut seçili öğeleri yükle (Maksimum 10 adet)
        foreach (var item in currentItems.Take(10))
        {
            _selectedItems.Add(new MarketItemDisplay
            {
                Id = item.Id,
                Name = item.Name,
                Symbol = item.Symbol,
                Category = item.Category,
                ColorHex = item.Color
            });
        }

        ReassignColors();
        UpdateCountText();
        BuildPresetCatalog();
    }

    private void BuildPresetCatalog()
    {
        PanelPresets.Children.Clear();

        var categories = new[]
        {
            new
            {
                Title = "🪙 Kripto Paralar (Binance)",
                Category = "crypto",
                Items = new[]
                {
                    ("BTCUSDT", "Bitcoin"),
                    ("ETHUSDT", "Ethereum"),
                    ("SOLUSDT", "Solana"),
                    ("BNBUSDT", "BNB"),
                    ("XRPUSDT", "Ripple"),
                    ("DOGEUSDT", "Dogecoin")
                }
            },
            new
            {
                Title = "📈 Borsa İstanbul (BIST)",
                Category = "bist",
                Items = new[]
                {
                    ("XU100.IS", "BIST 100"),
                    ("THYAO.IS", "THY"),
                    ("ASELS.IS", "Aselsan"),
                    ("GARAN.IS", "Garanti"),
                    ("EREGL.IS", "Erdemir"),
                    ("SISE.IS", "Şişecam"),
                    ("TUPRS.IS", "Tüpraş")
                }
            },
            new
            {
                Title = "🥇 Altın ve Döviz",
                Category = "gold",
                Items = new[]
                {
                    ("GC=F", "Altın (ONS)"),
                    ("GLD", "Altın (ETF)"),
                    ("USDTRY=X", "Dolar / TL"),
                    ("EURTRY=X", "Euro / TL")
                }
            }
        };

        foreach (var cat in categories)
        {
            var header = new TextBlock
            {
                Text = cat.Title,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                Margin = new Thickness(0, 8, 0, 6)
            };
            PanelPresets.Children.Add(header);

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            foreach (var (symbol, name) in cat.Items)
            {
                var btn = new Button
                {
                    Content = $"+ {name} ({symbol})",
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = new SolidColorBrush(Color.FromRgb(30, 36, 51)),
                    Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = (symbol, name, cat.Category)
                };

                btn.Click += PresetButton_Click;
                wrap.Children.Add(btn);
            }

            PanelPresets.Children.Add(wrap);
        }
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ValueTuple<string, string, string> tuple)
        {
            AddItem(tuple.Item1, tuple.Item2, tuple.Item3);
        }
    }

    private void AddItem(string symbol, string name, string category)
    {
        if (_selectedItems.Count >= 10)
        {
            MessageBox.Show(
                "En fazla 10 adet varlık seçebilirsiniz. Yeni eklemek için mevcut olanlardan birini silin.",
                "Maksimum Seçim Sınırı",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }

        if (_selectedItems.Any(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Bu varlık zaten listenizde ekli.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedItems.Add(new MarketItemDisplay
        {
            Id = symbol,
            Name = name,
            Symbol = symbol,
            Category = category
        });

        ReassignColors();
        UpdateCountText();
    }

    private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = _selectedItems.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                _selectedItems.Remove(item);
                ReassignColors();
                UpdateCountText();
            }
        }
    }

    private void ReassignColors()
    {
        for (int i = 0; i < _selectedItems.Count; i++)
        {
            _selectedItems[i].ColorHex = SlotColors[i % SlotColors.Length];
        }

        // Arayüzü yenile
        ItemsSelected.Items.Refresh();
    }

    private void UpdateCountText()
    {
        TxtSelectedCount.Text = $"{_selectedItems.Count} / 10 Seçildi";
    }

    private void BtnAddCustom_Click(object sender, RoutedEventArgs e)
    {
        string sym = TxtCustomSymbol.Text.Trim();
        if (string.IsNullOrWhiteSpace(sym) || sym.StartsWith("Örn:"))
        {
            MessageBox.Show("Lütfen geçerli bir sembol girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string cat = sym.EndsWith(".IS") ? "bist" : (sym.EndsWith("USDT") ? "crypto" : "gold");
        string name = sym;

        AddItem(sym, name, cat);
        TxtCustomSymbol.Text = string.Empty;
    }

    private void TxtCustomSymbol_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtCustomSymbol.Text.StartsWith("Örn:"))
        {
            TxtCustomSymbol.Text = string.Empty;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ResultItems = _selectedItems.Select(item => new MarketItemConfig
        {
            Id = item.Id,
            Name = item.Name,
            Symbol = item.Symbol,
            Category = item.Category,
            Color = item.ColorHex
        }).ToList();

        DialogResult = true;
        Close();
    }
}
