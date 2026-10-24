using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Media.Imaging;
using QRCoder;

namespace MacroDeck.Server.Services;

public class NetworkAdapterInfo
{
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string DisplayText => $"{Name}: {IpAddress}";
}

public static class NetworkHelper
{
    public static List<NetworkAdapterInfo> GetAllLocalIpAddresses()
    {
        var list = new List<NetworkAdapterInfo>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                // Sanal veya köprü adaptörleri atla
                string desc = ni.Description.ToLowerInvariant();
                string name = ni.Name.ToLowerInvariant();
                if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("wsl") ||
                    name.Contains("vethernet") || desc.Contains("bluetooth") || name.Contains("bluetooth"))
                    continue;

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ipStr = addr.Address.ToString();
                        // 169.254.x.x APIPA (bağlantısız otomatik IP) ve 127.x.x.x atla
                        if (!ipStr.StartsWith("169.254.") && !ipStr.StartsWith("127."))
                        {
                            list.Add(new NetworkAdapterInfo
                            {
                                Name = ni.Name,
                                IpAddress = ipStr
                            });
                        }
                    }
                }
            }
        }
        catch { }

        // Eğer liste boşsa doğrudan routing soket yöntemini dene
        if (list.Count == 0)
        {
            string fallback = GetDefaultGatewayIp();
            list.Add(new NetworkAdapterInfo { Name = "Varsayılan Ağ", IpAddress = fallback });
        }

        return list;
    }

    public static string GetDefaultGatewayIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                string ip = endPoint.Address.ToString();
                if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                {
                    return ip;
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public static string GetLocalIpAddress()
    {
        // 1. Önce aktif internet çıkışı olan yönlendirici IP'sini bul
        string gatewayIp = GetDefaultGatewayIp();
        if (gatewayIp != "127.0.0.1")
        {
            return gatewayIp;
        }

        // 2. Bulamazsa listedeki ilk gerçek adaptörü al
        var all = GetAllLocalIpAddresses();
        return all.FirstOrDefault()?.IpAddress ?? "127.0.0.1";
    }

    public static BitmapImage GenerateQrCode(string content)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrData);
        byte[] qrBytes = qrCode.GetGraphic(20);

        var bitmapImage = new BitmapImage();
        using var ms = new MemoryStream(qrBytes);
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = ms;
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        return bitmapImage;
    }
}
