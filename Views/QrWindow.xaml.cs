using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MacroDeck.Server.Services;

namespace MacroDeck.Server.Views;

public partial class QrWindow : Window
{
    private readonly int _port;
    private readonly List<NetworkAdapterInfo> _adapters;

    public QrWindow(string defaultIp, int port)
    {
        InitializeComponent();
        _port = port;

        _adapters = NetworkHelper.GetAllLocalIpAddresses();
        CmbAdapters.ItemsSource = _adapters;
        CmbAdapters.DisplayMemberPath = "DisplayText";

        // Varsayılan IP'yi seç
        var match = _adapters.FirstOrDefault(a => a.IpAddress == defaultIp) ?? _adapters.FirstOrDefault();
        if (match != null)
        {
            CmbAdapters.SelectedItem = match;
            UpdateQrCode(match.IpAddress);
        }
        else
        {
            UpdateQrCode(defaultIp);
        }
    }

    private void CmbAdapters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAdapters.SelectedItem is NetworkAdapterInfo adapter)
        {
            UpdateQrCode(adapter.IpAddress);
        }
    }

    private void UpdateQrCode(string ipAddress)
    {
        TxtConnectionInfo.Text = $"{ipAddress}:{_port}";

        var payload = new
        {
            ip = ipAddress,
            port = _port
        };
        string qrContent = JsonSerializer.Serialize(payload);

        try
        {
            var qrBmp = NetworkHelper.GenerateQrCode(qrContent);
            ImgQrCode.Source = qrBmp;
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"QR Kod oluşturulurken hata oluştu: {ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
