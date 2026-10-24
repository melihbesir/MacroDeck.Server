using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroDeck.Server.Models;

public class DeckButton
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("row")]
    public int Row { get; set; }

    [JsonPropertyName("col")]
    public int Col { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("appPath")]
    public string AppPath { get; set; } = string.Empty;

    [JsonPropertyName("appArgs")]
    public string AppArgs { get; set; } = string.Empty;

    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = "#1e222d";

    [JsonPropertyName("textColor")]
    public string TextColor { get; set; } = "#ffffff";

    [JsonPropertyName("iconBase64")]
    public string? IconBase64 { get; set; }

    [JsonPropertyName("iconPath")]
    public string? IconPath { get; set; }

    [JsonPropertyName("actionType")]
    public string ActionType { get; set; } = "app"; // "app", "weather", "market", "system" or "process"

    [JsonPropertyName("targetProcess")]
    public string TargetProcess { get; set; } = string.Empty;

    [JsonPropertyName("marketItems")]
    public List<MarketItemConfig>? MarketItems { get; set; }

    [JsonPropertyName("systemInterval")]
    public double SystemInterval { get; set; } = 1.0;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(AppPath) && string.IsNullOrWhiteSpace(IconBase64) && ActionType == "app" && string.IsNullOrWhiteSpace(TargetProcess) && (MarketItems == null || MarketItems.Count == 0);
}

public class CpuStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "CPU";

    [JsonPropertyName("usage")]
    public float Usage { get; set; }

    [JsonPropertyName("temp")]
    public float Temp { get; set; }

    [JsonPropertyName("clockMhz")]
    public float ClockMhz { get; set; }

    [JsonPropertyName("fanRpm")]
    public float FanRpm { get; set; }

    [JsonPropertyName("pumpRpm")]
    public float PumpRpm { get; set; }

    [JsonPropertyName("cores")]
    public List<float> Cores { get; set; } = new();

    [JsonPropertyName("history")]
    public List<float> History { get; set; } = new();
}

public class GpuStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "GPU";

    [JsonPropertyName("usage")]
    public float Usage { get; set; }

    [JsonPropertyName("temp")]
    public float Temp { get; set; }

    [JsonPropertyName("vramUsedGb")]
    public float VramUsedGb { get; set; }

    [JsonPropertyName("vramTotalGb")]
    public float VramTotalGb { get; set; }

    [JsonPropertyName("fanRpm")]
    public float FanRpm { get; set; }

    [JsonPropertyName("history")]
    public List<float> History { get; set; } = new();
}

public class RamSocketInfo
{
    [JsonPropertyName("locator")]
    public string Locator { get; set; } = string.Empty;

    [JsonPropertyName("capacityGb")]
    public float CapacityGb { get; set; }

    [JsonPropertyName("speedMhz")]
    public float SpeedMhz { get; set; }

    [JsonPropertyName("temp")]
    public float Temp { get; set; }
}

public class RamStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "RAM";

    [JsonPropertyName("usage")]
    public float Usage { get; set; }

    [JsonPropertyName("usedGb")]
    public float UsedGb { get; set; }

    [JsonPropertyName("totalGb")]
    public float TotalGb { get; set; }

    [JsonPropertyName("clockMhz")]
    public float ClockMhz { get; set; }

    [JsonPropertyName("temp")]
    public float Temp { get; set; }

    [JsonPropertyName("sockets")]
    public List<RamSocketInfo> Sockets { get; set; } = new();

    [JsonPropertyName("history")]
    public List<float> History { get; set; } = new();
}

public class DiskItemStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("driveLetter")]
    public string DriveLetter { get; set; } = string.Empty;

    [JsonPropertyName("usedGb")]
    public float UsedGb { get; set; }

    [JsonPropertyName("totalGb")]
    public float TotalGb { get; set; }

    [JsonPropertyName("usagePercent")]
    public float UsagePercent { get; set; }

    [JsonPropertyName("readSpeedMb")]
    public float ReadSpeedMb { get; set; }

    [JsonPropertyName("writeSpeedMb")]
    public float WriteSpeedMb { get; set; }

    [JsonPropertyName("temp")]
    public float Temp { get; set; }
}

public class FanItemStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rpm")]
    public float Rpm { get; set; }
}

public class SystemStats
{
    [JsonPropertyName("cpu")]
    public CpuStats Cpu { get; set; } = new();

    [JsonPropertyName("gpu")]
    public GpuStats Gpu { get; set; } = new();

    [JsonPropertyName("gpus")]
    public List<GpuStats> Gpus { get; set; } = new();

    [JsonPropertyName("ram")]
    public RamStats Ram { get; set; } = new();

    [JsonPropertyName("disks")]
    public List<DiskItemStats> Disks { get; set; } = new();

    [JsonPropertyName("fans")]
    public List<FanItemStats> Fans { get; set; } = new();
}

public class MarketItemConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "crypto"; // "crypto", "bist", "gold"

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty; // e.g. "BTCUSDT", "THYAO.IS", "GC=F"

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#38BDF8";
}

public class DeckConfig
{
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 5;

    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 4;

    [JsonPropertyName("cityName")]
    public string CityName { get; set; } = "İstanbul";

    [JsonPropertyName("marketItems")]
    public List<MarketItemConfig> MarketItems { get; set; } = new()
    {
        new MarketItemConfig { Id = "BTCUSDT", Name = "Bitcoin", Category = "crypto", Symbol = "BTCUSDT", Color = "#38BDF8" },
        new MarketItemConfig { Id = "ETHUSDT", Name = "Ethereum", Category = "crypto", Symbol = "ETHUSDT", Color = "#10B981" },
        new MarketItemConfig { Id = "XU100.IS", Name = "BIST 100", Category = "bist", Symbol = "XU100.IS", Color = "#F59E0B" },
        new MarketItemConfig { Id = "GC=F", Name = "Altın (ONS)", Category = "gold", Symbol = "GC=F", Color = "#F43F5E" }
    };

    [JsonPropertyName("buttons")]
    public List<DeckButton> Buttons { get; set; } = new();

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MacroDeck",
        "config.json"
    );

    public static DeckConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<DeckConfig>(json);
                if (config != null)
                {
                    config.EnsureGridSize();
                    return config;
                }
            }
        }
        catch { }

        var defaultConfig = new DeckConfig();
        defaultConfig.EnsureGridSize();
        return defaultConfig;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public void EnsureGridSize()
    {
        // 5x4 grid'deki tüm hücrelerin listede olmasını sağlar
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                if (!Buttons.Exists(b => b.Row == r && b.Col == c))
                {
                    Buttons.Add(new DeckButton
                    {
                        Id = $"btn_{r}_{c}",
                        Row = r,
                        Col = c,
                        Title = string.Empty,
                        AppPath = string.Empty
                    });
                }
            }
        }
    }

    public DeckButton? GetButton(int row, int col)
    {
        return Buttons.Find(b => b.Row == row && b.Col == col);
    }
}

public class SocketMessage
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
