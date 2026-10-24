using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MacroDeck.Server.Models;

public class ProcessStats
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("isAutoActiveWindow")]
    public bool IsAutoActiveWindow { get; set; } = true;

    // 1. CPU (Sadece uygulamanın kullanım yüzdesi)
    [JsonPropertyName("cpuPercent")]
    public float CpuPercent { get; set; }

    // 2. RAM (Uygulamanın RAM kullanımı ve sistem yüzdesi)
    [JsonPropertyName("ramMb")]
    public float RamMb { get; set; }

    [JsonPropertyName("ramPercent")]
    public float RamPercent { get; set; }

    // 3. GPU (Uygulamanın GPU 3D/Compute/Video kullanım yüzdesi)
    [JsonPropertyName("gpuPercent")]
    public float GpuPercent { get; set; }

    // 4. Disk (Okuma/Yazma hızları ve aktiflik yüzdesi)
    [JsonPropertyName("diskReadMb")]
    public float DiskReadMb { get; set; }

    [JsonPropertyName("diskWriteMb")]
    public float DiskWriteMb { get; set; }

    [JsonPropertyName("diskPercent")]
    public float DiskPercent { get; set; }

    // 5. Ağ / Network (Alış/Gönderiş hızları ve kullanım yüzdesi)
    [JsonPropertyName("netRecvMb")]
    public float NetRecvMb { get; set; }

    [JsonPropertyName("netSendMb")]
    public float NetSendMb { get; set; }

    [JsonPropertyName("netPercent")]
    public float NetPercent { get; set; }

    // 10 dakikalık geçmiş verileri (1 sn aralıkla 600 nokta)
    [JsonPropertyName("cpuHistory")]
    public List<float> CpuHistory { get; set; } = new();

    [JsonPropertyName("ramHistory")]
    public List<float> RamHistory { get; set; } = new();

    [JsonPropertyName("gpuHistory")]
    public List<float> GpuHistory { get; set; } = new();

    [JsonPropertyName("diskHistory")]
    public List<float> DiskHistory { get; set; } = new();

    [JsonPropertyName("netHistory")]
    public List<float> NetHistory { get; set; } = new();
}

public class ProcessItemInfo
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("cpuPercent")]
    public float CpuPercent { get; set; }

    [JsonPropertyName("ramMb")]
    public float RamMb { get; set; }
}

