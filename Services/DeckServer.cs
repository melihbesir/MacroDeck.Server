using System;
using System.Collections.Concurrent;
using System.Text.Json;
using Fleck;
using MacroDeck.Server.Models;

namespace MacroDeck.Server.Services;

public class DeckServer : IDisposable
{
    private WebSocketServer? _server;
    private readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();

    public int Port { get; } = 8765;
    public bool IsClientConnected => !_clients.IsEmpty;
    public int ConnectedClientCount => _clients.Count;

    public event Action<bool>? ConnectionStatusChanged;
    public event Action<string>? ButtonPressed;
    public event Action<int>? VolumeSetRequested;
    public event Action<bool, double>? SystemMonitorRequested;
    public event Action<bool, string?>? ProcessMonitorRequested;
    public event Action<int>? ProcessSelectionRequested;
    public event Action? ProcessListRequested;

    private static readonly JsonSerializerOptions _jsonCamelOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Start()
    {
        try
        {
            FleckLog.Level = LogLevel.Warn;
            _server = new WebSocketServer($"ws://0.0.0.0:{Port}")
            {
                RestartAfterListenError = true
            };

            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    _clients[socket.ConnectionInfo.Id] = socket;
                    ConnectionStatusChanged?.Invoke(true);
                };

                socket.OnClose = () =>
                {
                    _clients.TryRemove(socket.ConnectionInfo.Id, out _);
                    ConnectionStatusChanged?.Invoke(IsClientConnected);
                };

                socket.OnMessage = message =>
                {
                    HandleIncomingMessage(socket, message);
                };

                socket.OnError = ex =>
                {
                    System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}");
                };
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to start WebSocket server: {ex.Message}");
        }
    }

    private void HandleIncomingMessage(IWebSocketConnection socket, string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var eventProp))
                return;

            string eventType = eventProp.GetString() ?? string.Empty;

            switch (eventType)
            {
                case "BUTTON_PRESS":
                    if (root.TryGetProperty("id", out var idProp))
                    {
                        string buttonId = idProp.GetString() ?? string.Empty;
                        ButtonPressed?.Invoke(buttonId);
                    }
                    break;

                case "VOLUME_CHANGE":
                    if (root.TryGetProperty("value", out var valProp))
                    {
                        int volume = valProp.GetInt32();
                        VolumeSetRequested?.Invoke(volume);
                    }
                    break;

                case "REQUEST_SYNC":
                    // Telefon açıldığında konfigürasyon ister
                    // İstemciye mevcut durum MainWindow üzerinden gönderilebilir
                    break;

                case "START_SYSTEM_MONITOR":
                    double interval = 1.0;
                    if (root.TryGetProperty("interval", out var intProp))
                    {
                        interval = intProp.GetDouble();
                    }
                    SystemMonitorRequested?.Invoke(true, interval);
                    break;

                case "STOP_SYSTEM_MONITOR":
                    SystemMonitorRequested?.Invoke(false, 1.0);
                    break;

                case "START_PROCESS_MONITOR":
                    string? targetProc = null;
                    if (root.TryGetProperty("targetProcess", out var tpProp))
                    {
                        targetProc = tpProp.GetString();
                    }
                    ProcessMonitorRequested?.Invoke(true, targetProc);
                    break;

                case "STOP_PROCESS_MONITOR":
                    ProcessMonitorRequested?.Invoke(false, null);
                    break;

                case "SELECT_PROCESS":
                    if (root.TryGetProperty("pid", out var pPidProp))
                    {
                        ProcessSelectionRequested?.Invoke(pPidProp.GetInt32());
                    }
                    break;

                case "GET_PROCESS_LIST":
                    ProcessListRequested?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Parse incoming message error: {ex.Message}");
        }
    }

    public bool BroadcastConfig(DeckConfig config, int currentVolume)
    {
        if (!IsClientConnected)
            return false;

        try
        {
            var activeButtons = config.Buttons
                .Where(b => b.Row < config.Rows && b.Col < config.Columns)
                .ToList();

            var payload = new
            {
                @event = "GRID_CONFIG",
                columns = config.Columns,
                rows = config.Rows,
                currentVolume = currentVolume,
                buttons = activeButtons,
                marketItems = config.MarketItems
            };

            string json = JsonSerializer.Serialize(payload);
            Broadcast(json);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BroadcastConfig error: {ex.Message}");
            return false;
        }
    }

    public bool BroadcastMarketConfig(List<MarketItemConfig> items)
    {
        if (!IsClientConnected)
            return false;

        try
        {
            var payload = new
            {
                @event = "MARKET_CONFIG",
                items = items
            };

            string json = JsonSerializer.Serialize(payload);
            Broadcast(json);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BroadcastMarketConfig error: {ex.Message}");
            return false;
        }
    }

    public void BroadcastVolumeSync(int volumePercent)
    {
        if (!IsClientConnected)
            return;

        try
        {
            var payload = new
            {
                @event = "VOLUME_SYNC",
                value = volumePercent
            };

            string json = JsonSerializer.Serialize(payload);
            Broadcast(json);
        }
        catch { }
    }

    public void BroadcastWeather(WeatherData data)
    {
        if (!IsClientConnected)
            return;

        try
        {
            var payload = new
            {
                @event = "WEATHER_DATA",
                data = data
            };

            string json = JsonSerializer.Serialize(payload);
            Broadcast(json);
        }
        catch { }
    }

    public void BroadcastSystemStats(SystemStats stats)
    {
        if (!IsClientConnected)
            return;

        try
        {
            var payload = new
            {
                @event = "SYSTEM_STATS",
                data = stats
            };

            string json = JsonSerializer.Serialize(payload);
            Broadcast(json);
        }
        catch { }
    }

    public void BroadcastProcessStats(MacroDeck.Server.Models.ProcessStats stats)
    {
        if (!IsClientConnected)
            return;

        try
        {
            var payload = new
            {
                @event = "PROCESS_STATS",
                data = stats
            };

            string json = JsonSerializer.Serialize(payload, _jsonCamelOptions);
            Broadcast(json);
        }
        catch { }
    }

    public void BroadcastProcessList(List<MacroDeck.Server.Models.ProcessItemInfo> processes)
    {
        if (!IsClientConnected)
            return;

        try
        {
            var payload = new
            {
                @event = "PROCESS_LIST",
                processes = processes
            };

            string json = JsonSerializer.Serialize(payload, _jsonCamelOptions);
            Broadcast(json);
        }
        catch { }
    }


    private void Broadcast(string message)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsAvailable)
            {
                client.Send(message);
            }
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            try { client.Close(); } catch { }
        }
        _clients.Clear();
        _server?.Dispose();
    }
}
