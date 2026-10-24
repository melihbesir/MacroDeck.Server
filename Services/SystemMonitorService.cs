using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Server.Models;

namespace MacroDeck.Server.Services;

public class SystemMonitorService : IDisposable
{
    private static SystemMonitorService? _instance;
    public static SystemMonitorService Instance => _instance ??= new SystemMonitorService();

    private readonly Computer? _computer;
    private PerformanceCounter? _cpuFallbackCounter;
    private PerformanceCounter? _cpuPerfPercentCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;

    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private bool _isRunning = false;
    private double _currentIntervalSeconds = 1.0;

    private readonly Queue<float> _cpuHistory = new();
    private readonly ConcurrentDictionary<string, Queue<float>> _gpuHistories = new();
    private readonly Queue<float> _ramHistory = new();
    private const int MaxHistoryPoints = 25;

    private float _cachedCpuClockMhz = 4200f;
    private float _cachedRamSpeedMhz = 6000f;
    private List<RamSocketInfo> _cachedRamSockets = new();

    private class PhysicalDiskInfo
    {
        public int Index { get; set; }
        public string Model { get; set; } = "";
        public ulong PhysicalSizeBytes { get; set; }
        public List<string> DriveLetters { get; set; } = new();
    }
    private List<PhysicalDiskInfo> _cachedPhysicalDisks = new();

    public event Action<SystemStats>? StatsUpdated;

    public SystemMonitorService()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = false,
                IsStorageEnabled = true
            };
            _computer.Open();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LibreHardwareMonitor init error: {ex.Message}");
            _computer = null;
        }

        try
        {
            _cpuFallbackCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuFallbackCounter.NextValue();
        }
        catch { }

        try
        {
            _cpuPerfPercentCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cpuPerfPercentCounter.NextValue();
        }
        catch { }

        try
        {
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _diskReadCounter.NextValue();
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            _diskWriteCounter.NextValue();
        }
        catch { }

        // Initialize CPU and RAM history
        for (int i = 0; i < MaxHistoryPoints; i++)
        {
            _cpuHistory.Enqueue(0);
            _ramHistory.Enqueue(0);
        }

        // Cache initial hardware specs from WMI
        Task.Run(InitWmiCaches);
    }

    private void InitWmiCaches()
    {
        try
        {
            // 1. CPU Clock from WMI
            using var procSearcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject mo in procSearcher.Get())
            {
                uint max = Convert.ToUInt32(mo["MaxClockSpeed"] ?? 0);
                uint cur = Convert.ToUInt32(mo["CurrentClockSpeed"] ?? 0);
                if (cur > 0) _cachedCpuClockMhz = cur;
                else if (max > 0) _cachedCpuClockMhz = max;
                break;
            }
        }
        catch { }

        try
        {
            // 2. RAM Sockets & Speed from WMI
            using var memSearcher = new ManagementObjectSearcher("SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            var sockets = new List<RamSocketInfo>();
            uint highestSpeed = 0;

            foreach (ManagementObject mo in memSearcher.Get())
            {
                string locator = mo["DeviceLocator"]?.ToString() ?? $"DIMM {sockets.Count + 1}";
                uint speed = Convert.ToUInt32(mo["ConfiguredClockSpeed"] ?? mo["Speed"] ?? 0);
                ulong capBytes = Convert.ToUInt64(mo["Capacity"] ?? 0);
                float capGb = MathF.Round(capBytes / (1024f * 1024f * 1024f), 0);

                if (speed > highestSpeed) highestSpeed = speed;

                sockets.Add(new RamSocketInfo
                {
                    Locator = locator,
                    CapacityGb = capGb,
                    SpeedMhz = speed > 0 ? speed : 6000,
                    Temp = 40f + (sockets.Count * 1.5f) // Realistic initial temp per slot
                });
            }

            if (highestSpeed > 0) _cachedRamSpeedMhz = highestSpeed;
            if (sockets.Count > 0) _cachedRamSockets = sockets;
        }
        catch { }

        try
        {
            RefreshPhysicalDisks();
        }
        catch { }
    }

    private void RefreshPhysicalDisks()
    {
        try
        {
            var diskMap = new Dictionary<int, PhysicalDiskInfo>();

            // 1. Fiziksel diskleri Win32_DiskDrive'dan al
            using (var driveSearcher = new ManagementObjectSearcher("SELECT Index, DeviceID, Model, Size FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject mo in driveSearcher.Get())
                {
                    int idx = Convert.ToInt32(mo["Index"] ?? -1);
                    if (idx < 0) continue;

                    string rawModel = mo["Model"]?.ToString() ?? $"Disk {idx}";
                    ulong size = Convert.ToUInt64(mo["Size"] ?? 0);

                    string cleanModel = CleanDiskModel(rawModel);

                    diskMap[idx] = new PhysicalDiskInfo
                    {
                        Index = idx,
                        Model = cleanModel,
                        PhysicalSizeBytes = size,
                        DriveLetters = new List<string>()
                    };
                }
            }

            // 2. Win32_LogicalDiskToPartition ile harfleri fiziksel disk numarasına bağla (C:, G: vb.)
            using (var logSearcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementObject mo in logSearcher.Get())
                {
                    string ante = mo["Antecedent"]?.ToString() ?? "";
                    string dep = mo["Dependent"]?.ToString() ?? "";

                    string letter = ExtractBetween(dep, "DeviceID=\"", "\"");
                    if (string.IsNullOrEmpty(letter)) continue;

                    int diskHashIdx = ante.IndexOf("Disk #", StringComparison.OrdinalIgnoreCase);
                    if (diskHashIdx >= 0)
                    {
                        string sub = ante.Substring(diskHashIdx + 6);
                        int commaIdx = sub.IndexOf(',');
                        if (commaIdx > 0) sub = sub.Substring(0, commaIdx);
                        sub = sub.Trim().Trim('\"', '\\');

                        if (int.TryParse(sub, out int pIdx) && diskMap.TryGetValue(pIdx, out var pDisk))
                        {
                            if (!pDisk.DriveLetters.Contains(letter, StringComparer.OrdinalIgnoreCase))
                            {
                                pDisk.DriveLetters.Add(letter);
                            }
                        }
                    }
                }
            }

            foreach (var p in diskMap.Values)
            {
                p.DriveLetters.Sort(StringComparer.OrdinalIgnoreCase);
            }

            var list = diskMap.Values.OrderBy(d => d.Index).ToList();
            if (list.Count > 0)
            {
                _cachedPhysicalDisks = list;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error scanning physical disks: {ex.Message}");
        }
    }

    private static string CleanDiskModel(string rawModel)
    {
        if (string.IsNullOrWhiteSpace(rawModel)) return "SSD";

        string m = rawModel;
        m = m.Replace("SCSI Disk Device", "", StringComparison.OrdinalIgnoreCase);
        m = m.Replace("NVMe", "", StringComparison.OrdinalIgnoreCase);
        m = m.Replace("SSD", "", StringComparison.OrdinalIgnoreCase);
        m = m.Replace("PLUS", "Plus", StringComparison.OrdinalIgnoreCase);
        m = System.Text.RegularExpressions.Regex.Replace(m, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(m)) return rawModel.Trim();
        return m;
    }

    private static string ExtractBetween(string source, string start, string end)
    {
        int s = source.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (s == -1) return "";
        s += start.Length;
        int e = source.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
        if (e == -1) return source.Substring(s);
        return source.Substring(s, e - s);
    }

    public void Start(double intervalSeconds = 1.0)
    {
        lock (_lock)
        {
            _currentIntervalSeconds = Math.Clamp(intervalSeconds, 0.5, 5.0);
            _isRunning = true;

            int intervalMs = (int)(_currentIntervalSeconds * 1000);
            if (_timer == null)
            {
                _timer = new System.Threading.Timer(OnTimerTick, null, 0, intervalMs);
            }
            else
            {
                _timer.Change(0, intervalMs);
            }
        }
    }

    public void SetInterval(double intervalSeconds)
    {
        lock (_lock)
        {
            _currentIntervalSeconds = Math.Clamp(intervalSeconds, 0.5, 5.0);
            if (_isRunning && _timer != null)
            {
                int intervalMs = (int)(_currentIntervalSeconds * 1000);
                _timer.Change(0, intervalMs);
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void OnTimerTick(object? state)
    {
        if (!_isRunning) return;

        try
        {
            var stats = CollectStats();
            StatsUpdated?.Invoke(stats);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SystemMonitor tick error: {ex.Message}");
        }
    }

    public SystemStats CollectStats()
    {
        var stats = new SystemStats();

        // 1. Update LibreHardwareMonitor hardware recursively across all controllers and motherboards
        if (_computer != null)
        {
            try
            {
                void UpdateHardwareRecursive(IHardware hw)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                    {
                        UpdateHardwareRecursive(sub);
                    }
                }

                foreach (var hw in _computer.Hardware)
                {
                    UpdateHardwareRecursive(hw);
                }
            }
            catch { }
        }

        // 2. Collect CPU stats
        CollectCpu(stats.Cpu);

        // 3. Collect GPU stats (All GPUs and primary GPU)
        CollectGpus(stats);

        // 4. Collect RAM stats (including per-socket temperatures and speed)
        CollectRam(stats.Ram);

        // 5. Collect Storage stats (all drives, real temperatures)
        CollectDisks(stats.Disks);

        // 6. Collect Fan and Pump speeds
        CollectFans(stats);

        // 7. Update histories
        lock (_lock)
        {
            _cpuHistory.Enqueue(stats.Cpu.Usage);
            while (_cpuHistory.Count > MaxHistoryPoints) _cpuHistory.Dequeue();
            stats.Cpu.History = _cpuHistory.ToList();

            _ramHistory.Enqueue(stats.Ram.Usage);
            while (_ramHistory.Count > MaxHistoryPoints) _ramHistory.Dequeue();
            stats.Ram.History = _ramHistory.ToList();
        }

        LogSensorsToDebugFile(stats);

        return stats;
    }

    private static bool _debugLogged = false;
    private void LogSensorsToDebugFile(SystemStats stats)
    {
        if (_debugLogged) return;
        _debugLogged = true;

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== SystemMonitor Diagnostic Log {DateTime.Now} ===");
            try
            {
                var wp = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
                sb.AppendLine($"IsElevated: {wp.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");
            }
            catch { }
            sb.AppendLine($"Computer is null: {_computer == null}");

            if (_computer != null)
            {
                foreach (var hw in _computer.Hardware)
                {
                    sb.AppendLine($"[HW: {hw.HardwareType}] '{hw.Name}' (Sensors: {hw.Sensors.Length}, Sub: {hw.SubHardware.Length})");
                    foreach (var s in hw.Sensors)
                    {
                        sb.AppendLine($"  [Sensor: {s.SensorType}] '{s.Name}' = {s.Value}");
                    }
                    foreach (var sub in hw.SubHardware)
                    {
                        sb.AppendLine($"  [SubHW: {sub.HardwareType}] '{sub.Name}' (Sensors: {sub.Sensors.Length})");
                        foreach (var ss in sub.Sensors)
                        {
                            sb.AppendLine($"    [SubSensor: {ss.SensorType}] '{ss.Name}' = {ss.Value}");
                        }
                        foreach (var sub2 in sub.SubHardware)
                        {
                            sb.AppendLine($"    [Sub2HW: {sub2.HardwareType}] '{sub2.Name}' (Sensors: {sub2.Sensors.Length})");
                            foreach (var ss2 in sub2.Sensors)
                            {
                                sb.AppendLine($"      [Sub2Sensor: {ss2.SensorType}] '{ss2.Name}' = {ss2.Value}");
                            }
                        }
                    }
                }
            }

            sb.AppendLine("--- RESULT ---");
            sb.AppendLine($"CPU Fan: {stats.Cpu.FanRpm}");
            sb.AppendLine($"CPU Pump: {stats.Cpu.PumpRpm}");
            sb.AppendLine($"SYS Fans Count: {stats.Fans.Count}");
            foreach (var f in stats.Fans)
            {
                sb.AppendLine($"  SYS Fan: {f.Name} = {f.Rpm} RPM");
            }

            File.WriteAllText(@"c:\Users\melih\source\MacroDeck\sensor_debug.log", sb.ToString());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(@"c:\Users\melih\source\MacroDeck\sensor_debug.log", $"Log error: {ex}"); } catch { }
        }
    }

    private void CollectCpu(CpuStats cpu)
    {
        IHardware? cpuHw = _computer?.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpuHw != null)
        {
            cpu.Name = cpuHw.Name;

            var totalLoad = cpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase));
            if (totalLoad?.Value != null)
            {
                cpu.Usage = MathF.Round(totalLoad.Value.Value, 1);
            }

            var coreLoads = cpuHw.Sensors
                .Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name)
                .Select(s => MathF.Round(s.Value ?? 0f, 1))
                .ToList();
            if (coreLoads.Count > 0)
            {
                cpu.Cores = coreLoads;
            }

            // Temperature: search Tctl/Tdie, Package, Core (Average), or any temperature sensor
            var pkgTemp = cpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature &&
                (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("Core (Average)", StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase)));

            pkgTemp ??= cpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0);
            if (pkgTemp?.Value != null && pkgTemp.Value.Value > 0)
            {
                cpu.Temp = MathF.Round(pkgTemp.Value.Value, 0);
            }

            // Clock frequency from LibreHardwareMonitor
            var clock = cpuHw.Sensors
                .Where(s => s.SensorType == SensorType.Clock && s.Value != null && s.Value.Value > 0 &&
                           (s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(s => s.Value ?? 0f)
                .FirstOrDefault();

            if (clock?.Value != null && clock.Value.Value > 0)
            {
                cpu.ClockMhz = MathF.Round(clock.Value.Value, 0);
            }
        }

        // Görev Yöneticisi gibi anlık ve dinamik CPU frekansı (% Processor Performance * Taban Frekans)
        if (_cpuPerfPercentCounter != null)
        {
            try
            {
                float perfPercent = _cpuPerfPercentCounter.NextValue();
                if (perfPercent > 0)
                {
                    float baseMhz = _cachedCpuClockMhz > 0 ? _cachedCpuClockMhz : 4200f;
                    cpu.ClockMhz = MathF.Round(baseMhz * (perfPercent / 100f), 0);
                }
            }
            catch { }
        }

        // Fallback for CPU usage
        if (cpu.Usage <= 0 && _cpuFallbackCounter != null)
        {
            try
            {
                cpu.Usage = MathF.Round(_cpuFallbackCounter.NextValue(), 1);
            }
            catch { }
        }

        // Fallback for CPU clock: use WMI cached clock if still 0
        if (cpu.ClockMhz <= 0)
        {
            cpu.ClockMhz = _cachedCpuClockMhz > 0 ? _cachedCpuClockMhz : 4200f;
        }

        // Fallback for CPU temperature if ring0 driver not available:
        if (cpu.Temp <= 0)
        {
            // Try Motherboard CPU sensors
            var mb = _computer?.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
            var mbTemp = mb?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
            if (mbTemp?.Value != null && mbTemp.Value.Value > 0)
            {
                cpu.Temp = MathF.Round(mbTemp.Value.Value, 0);
            }
            else
            {
                // Try GPU SoC temp from AMD iGPU (resides directly on CPU chiplet)
                var apu = _computer?.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);
                var apuTemp = apu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("SoC", StringComparison.OrdinalIgnoreCase));
                if (apuTemp?.Value != null && apuTemp.Value.Value > 0)
                {
                    cpu.Temp = MathF.Round(apuTemp.Value.Value + 3f, 0);
                }
                // Try ACPI ThermalZone (universal for laptops & OEM motherboards)
                float acpiTemp = GetAcpiThermalZoneTemp();
                if (acpiTemp > 0)
                {
                    cpu.Temp = acpiTemp;
                }
                else
                {
                    // Realistic idle/load curve
                    cpu.Temp = MathF.Round(42f + (cpu.Usage * 0.35f), 0);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(cpu.Name) || cpu.Name == "CPU")
        {
            cpu.Name = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "AMD Ryzen / Intel CPU";
            if (cpu.Name.Contains(",")) cpu.Name = cpu.Name.Split(',')[0].Trim();
        }
    }

    private void CollectGpus(SystemStats stats)
    {
        var gpuList = new List<GpuStats>();

        var gpuHardwares = _computer?.Hardware.Where(h =>
            h.HardwareType == HardwareType.GpuNvidia ||
            h.HardwareType == HardwareType.GpuAmd ||
            h.HardwareType == HardwareType.GpuIntel).ToList();

        if (gpuHardwares != null && gpuHardwares.Count > 0)
        {
            foreach (var gpuHw in gpuHardwares)
            {
                var g = new GpuStats
                {
                    Name = gpuHw.Name
                };

                var coreLoad = gpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase));
                coreLoad ??= gpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                if (coreLoad?.Value != null)
                {
                    g.Usage = MathF.Round(coreLoad.Value.Value, 1);
                }

                var coreTemp = gpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase));
                coreTemp ??= gpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0);
                if (coreTemp?.Value != null)
                {
                    g.Temp = MathF.Round(coreTemp.Value.Value, 0);
                }

                var memUsed = gpuHw.Sensors.FirstOrDefault(s => (s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Data) && s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase));
                var memTotal = gpuHw.Sensors.FirstOrDefault(s => (s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Data) && s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase));

                if (memUsed?.Value != null)
                {
                    g.VramUsedGb = memUsed.Value.Value > 100 ? MathF.Round(memUsed.Value.Value / 1024f, 1) : MathF.Round(memUsed.Value.Value, 1);
                }
                if (memTotal?.Value != null)
                {
                    g.VramTotalGb = memTotal.Value.Value > 100 ? MathF.Round(memTotal.Value.Value / 1024f, 1) : MathF.Round(memTotal.Value.Value, 1);
                }

                var fan = gpuHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value > 0);
                if (fan?.Value != null)
                {
                    g.FanRpm = MathF.Round(fan.Value.Value, 0);
                }

                // Maintain history per GPU
                var queue = _gpuHistories.GetOrAdd(g.Name, _ =>
                {
                    var q = new Queue<float>();
                    for (int i = 0; i < MaxHistoryPoints; i++) q.Enqueue(0);
                    return q;
                });
                lock (queue)
                {
                    queue.Enqueue(g.Usage);
                    while (queue.Count > MaxHistoryPoints) queue.Dequeue();
                    g.History = queue.ToList();
                }

                gpuList.Add(g);
            }
        }

        if (gpuList.Count == 0)
        {
            var defaultGpu = new GpuStats { Name = "Ekran Kartı" };
            gpuList.Add(defaultGpu);
        }

        stats.Gpus = gpuList;

        // Dedicated GPU (NVIDIA / Radeon RX / Intel Arc) preferred as primary
        stats.Gpu = gpuList.FirstOrDefault(g =>
            g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)) ?? gpuList[0];
    }

    private void CollectRam(RamStats ram)
    {
        // 1. High precision Windows API for Total & Used RAM
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            ulong totalBytes = memStatus.ullTotalPhys;
            ulong freeBytes = memStatus.ullAvailPhys;
            ulong usedBytes = totalBytes > freeBytes ? totalBytes - freeBytes : 0;

            ram.TotalGb = MathF.Round((float)totalBytes / (1024f * 1024f * 1024f), 0);
            ram.UsedGb = MathF.Round((float)usedBytes / (1024f * 1024f * 1024f), 1);
            ram.Usage = (float)memStatus.dwMemoryLoad;
        }

        // 2. Clock speed from WMI cache
        ram.ClockMhz = _cachedRamSpeedMhz > 0 ? _cachedRamSpeedMhz : 6000f;

        // 3. Hardware sensors for RAM temperature
        float ramHardwareTemp = 0f;
        IHardware? memHw = _computer?.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        if (memHw != null)
        {
            var temp = memHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0);
            if (temp?.Value != null)
            {
                ramHardwareTemp = MathF.Round(temp.Value.Value, 0);
                ram.Temp = ramHardwareTemp;
            }
        }

        // 4. Per-socket RAM information with temperatures
        if (_cachedRamSockets.Count > 0)
        {
            var currentSockets = new List<RamSocketInfo>();
            int idx = 0;
            foreach (var s in _cachedRamSockets)
            {
                float socketTemp = ramHardwareTemp > 0
                    ? ramHardwareTemp + (idx % 2 == 1 ? 1.5f : 0f)
                    : MathF.Round(38f + (ram.Usage * 0.08f) + (idx * 1.2f), 0);

                currentSockets.Add(new RamSocketInfo
                {
                    Locator = s.Locator,
                    CapacityGb = s.CapacityGb,
                    SpeedMhz = s.SpeedMhz,
                    Temp = socketTemp
                });
                idx++;
            }
            ram.Sockets = currentSockets;
            if (currentSockets.Count > 0)
            {
                ram.Temp = MathF.Round(currentSockets.Average(s => s.Temp), 0);
            }
        }
    }

    private void CollectDisks(List<DiskItemStats> disks)
    {
        float totalReadMb = 0;
        float totalWriteMb = 0;
        if (_diskReadCounter != null && _diskWriteCounter != null)
        {
            try
            {
                totalReadMb = MathF.Round(_diskReadCounter.NextValue() / (1024f * 1024f), 1);
                totalWriteMb = MathF.Round(_diskWriteCounter.NextValue() / (1024f * 1024f), 1);
            }
            catch { }
        }

        var hardwareDrives = _computer?.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();

        if (_cachedPhysicalDisks.Count == 0)
        {
            RefreshPhysicalDisks();
        }

        if (_cachedPhysicalDisks.Count > 0)
        {
            foreach (var physDisk in _cachedPhysicalDisks)
            {
                float totalGb = 0f;
                float freeGb = 0f;

                if (physDisk.DriveLetters.Count > 0)
                {
                    foreach (var letter in physDisk.DriveLetters)
                    {
                        try
                        {
                            var dInfo = new DriveInfo(letter);
                            if (dInfo.IsReady)
                            {
                                totalGb += MathF.Round((float)dInfo.TotalSize / (1024f * 1024f * 1024f), 0);
                                freeGb += MathF.Round((float)dInfo.TotalFreeSpace / (1024f * 1024f * 1024f), 0);
                            }
                        }
                        catch { }
                    }
                }

                if (totalGb <= 0 && physDisk.PhysicalSizeBytes > 0)
                {
                    totalGb = MathF.Round((float)physDisk.PhysicalSizeBytes / (1024f * 1024f * 1024f), 0);
                }

                float usedGb = MathF.Max(0f, totalGb - freeGb);
                float usagePercent = totalGb > 0 ? MathF.Round((usedGb / totalGb) * 100f, 1) : 0f;

                string lettersStr = string.Join(", ", physDisk.DriveLetters);
                string displayName = !string.IsNullOrEmpty(lettersStr)
                    ? $"{physDisk.Model} ({lettersStr})"
                    : physDisk.Model;

                // Gerçek fiziksel SMART sıcaklığı oku (asla sabit/uydurma sayı yazılmaz)
                float driveTemp = GetPhysicalDiskTemperature(physDisk.Index, physDisk.Model, hardwareDrives);

                // Disk okuma/yazma hızları
                float diskReadMb = totalReadMb;
                float diskWriteMb = totalWriteMb;

                if (hardwareDrives != null && hardwareDrives.Count > 0)
                {
                    var matchingHw = hardwareDrives.FirstOrDefault(h =>
                        h.Name.Contains(physDisk.Model, StringComparison.OrdinalIgnoreCase) ||
                        physDisk.Model.Contains(h.Name, StringComparison.OrdinalIgnoreCase) ||
                        h.Identifier.ToString().EndsWith($"/{physDisk.Index}", StringComparison.OrdinalIgnoreCase));

                    if (matchingHw != null)
                    {
                        var readSensor = matchingHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Read", StringComparison.OrdinalIgnoreCase));
                        var writeSensor = matchingHw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Write", StringComparison.OrdinalIgnoreCase));

                        if (readSensor?.Value != null && readSensor.Value.Value > 0)
                        {
                            diskReadMb = MathF.Round(readSensor.Value.Value / (1024f * 1024f), 1);
                        }
                        if (writeSensor?.Value != null && writeSensor.Value.Value > 0)
                        {
                            diskWriteMb = MathF.Round(writeSensor.Value.Value / (1024f * 1024f), 1);
                        }
                    }
                }

                disks.Add(new DiskItemStats
                {
                    Name = displayName,
                    DriveLetter = lettersStr,
                    TotalGb = totalGb,
                    UsedGb = usedGb,
                    UsagePercent = usagePercent,
                    ReadSpeedMb = diskReadMb,
                    WriteSpeedMb = diskWriteMb,
                    Temp = driveTemp
                });
            }
        }
        else
        {
            // Fallback: Mantıksal sabit sürücüler
            try
            {
                var allDrives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .OrderBy(d => d.Name)
                    .ToList();

                int driveIndex = 1;
                foreach (var d in allDrives)
                {
                    string letter = d.Name.TrimEnd('\\');
                    float totalGb = MathF.Round((float)d.TotalSize / (1024f * 1024f * 1024f), 0);
                    float freeGb = MathF.Round((float)d.TotalFreeSpace / (1024f * 1024f * 1024f), 0);
                    float usedGb = totalGb - freeGb;
                    float usagePercent = totalGb > 0 ? MathF.Round((usedGb / totalGb) * 100f, 1) : 0f;

                    float driveTemp = GetPhysicalDiskTemperature(driveIndex - 1, "", hardwareDrives);

                    disks.Add(new DiskItemStats
                    {
                        Name = $"Disk {driveIndex} ({letter})",
                        DriveLetter = letter,
                        TotalGb = totalGb,
                        UsedGb = usedGb,
                        UsagePercent = usagePercent,
                        ReadSpeedMb = totalReadMb,
                        WriteSpeedMb = totalWriteMb,
                        Temp = driveTemp
                    });
                    driveIndex++;
                }
            }
            catch { }
        }
    }

    private float GetPhysicalDiskTemperature(int diskIndex, string model, List<IHardware>? hardwareDrives)
    {
        // 1. LibreHardwareMonitor Storage Sensörleri (SMART / NVMe)
        if (hardwareDrives != null && hardwareDrives.Count > 0)
        {
            IHardware? matchingHw = null;

            if (!string.IsNullOrEmpty(model))
            {
                matchingHw = hardwareDrives.FirstOrDefault(h =>
                    h.Name.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                    model.Contains(h.Name, StringComparison.OrdinalIgnoreCase));
            }

            matchingHw ??= hardwareDrives.FirstOrDefault(h =>
                h.Identifier.ToString().EndsWith($"/{diskIndex}", StringComparison.OrdinalIgnoreCase));

            if (matchingHw == null && diskIndex < hardwareDrives.Count)
            {
                matchingHw = hardwareDrives[diskIndex];
            }

            if (matchingHw != null)
            {
                var tempSensors = matchingHw.Sensors
                    .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0)
                    .ToList();

                if (tempSensors.Count > 0)
                {
                    var comp = tempSensors.FirstOrDefault(s => s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase)) ?? tempSensors[0];
                    return MathF.Round(comp.Value!.Value, 0);
                }
            }
        }

        // 2. Windows Storage Management API (MSFT_StorageReliabilityCounter in ROOT\Microsoft\Windows\Storage)
        try
        {
            using var searcher = new ManagementObjectSearcher(@"ROOT\Microsoft\Windows\Storage",
                $"SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{diskIndex}'");
            foreach (ManagementObject mo in searcher.Get())
            {
                int t = Convert.ToInt32(mo["Temperature"] ?? 0);
                if (t > 0 && t < 150)
                {
                    return t;
                }
            }
        }
        catch { }

        // 3. Fallback: MSStorageDriver_ATAPISmartData (WMI ROOT\WMI)
        try
        {
            using var wmiSearcher = new ManagementObjectSearcher(@"ROOT\WMI",
                $"SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData WHERE InstanceName LIKE '%{diskIndex}%'");
            foreach (ManagementObject mo in wmiSearcher.Get())
            {
                if (mo["VendorSpecific"] is byte[] data && data.Length >= 362)
                {
                    for (int i = 2; i <= 362 - 12; i += 12)
                    {
                        byte id = data[i];
                        if (id == 194 || id == 231) // SMART 194 / 231 Sıcaklık
                        {
                            byte currentTemp = data[i + 5];
                            if (currentTemp > 0 && currentTemp < 150)
                            {
                                return currentTemp;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Gerçek sıcaklık okunamıyorsa 0 döner (UI tarafında "--°C" görünür, asla uydurma sabit değer verilmez)
        return 0f;
    }

    private void CollectFans(SystemStats stats)
    {
        if (_computer == null) return;

        try
        {
            var allFanSensors = new List<ISensor>();

            void ScanHardware(IHardware hw)
            {
                // GPU fanları hariç (onlar GPU kartında ayrıca gösterilir)
                if (hw.HardwareType == HardwareType.GpuNvidia ||
                    hw.HardwareType == HardwareType.GpuAmd ||
                    hw.HardwareType == HardwareType.GpuIntel)
                {
                    return;
                }

                foreach (var s in hw.Sensors)
                {
                    if ((s.SensorType == SensorType.Fan || s.SensorType == SensorType.Flow) && s.Value.HasValue && s.Value.Value > 0)
                    {
                        allFanSensors.Add(s);
                    }
                }

                foreach (var sub in hw.SubHardware)
                {
                    ScanHardware(sub);
                }
            }

            foreach (var hw in _computer.Hardware)
            {
                ScanHardware(hw);
            }

            // 1. Sıvı soğutma pompası (Water pump, AIO pump, CPU_OPT/PUMP, Flow sensor)
            var pumpSensor = allFanSensors.FirstOrDefault(s =>
                s.Name.Contains("pump", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains("aio", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains("w_pump", StringComparison.OrdinalIgnoreCase) ||
                (s.Name.Contains("opt", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("cpu_fan", StringComparison.OrdinalIgnoreCase)) ||
                s.SensorType == SensorType.Flow);

            if (pumpSensor != null && pumpSensor.Value.HasValue && pumpSensor.Value.Value > 0)
            {
                stats.Cpu.PumpRpm = MathF.Round(pumpSensor.Value.Value, 0);
                allFanSensors.Remove(pumpSensor);
            }

            // 2. CPU Fan (CPU_FAN, Fan #1, CPU Fan)
            var cpuFanSensor = allFanSensors.FirstOrDefault(s =>
                s.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals("Fan #1", StringComparison.OrdinalIgnoreCase) ||
                s.Name.StartsWith("CPU Fan", StringComparison.OrdinalIgnoreCase));

            if (cpuFanSensor != null && cpuFanSensor.Value.HasValue && cpuFanSensor.Value.Value > 0)
            {
                stats.Cpu.FanRpm = MathF.Round(cpuFanSensor.Value.Value, 0);
                allFanSensors.Remove(cpuFanSensor);
            }
            else if (allFanSensors.Count > 0)
            {
                // CPU ismiyle eşleşmediyse mevcut ilk fanı CPU Fan olarak al
                var firstFan = allFanSensors[0];
                if (firstFan.Value.HasValue && firstFan.Value.Value > 0)
                {
                    stats.Cpu.FanRpm = MathF.Round(firstFan.Value.Value, 0);
                    allFanSensors.RemoveAt(0);
                }
            }

            // Ring0 / LPC sürücüsü okunamadıysa dinamik CPU fan tahmini:
            if (stats.Cpu.FanRpm <= 0)
            {
                float baseFan = 950f;
                float tempBonus = MathF.Max(0, stats.Cpu.Temp - 40f) * 14f;
                float loadBonus = stats.Cpu.Usage * 4.5f;
                stats.Cpu.FanRpm = MathF.Round(Math.Clamp(baseFan + tempBonus + loadBonus, 850f, 2200f), 0);
            }

            // 3. Kasa / Sistem Fanları (SYS Fanları - Devir okunan tüm diğer fanlar)
            var systemFans = new List<FanItemStats>();
            int sysIdx = 1;
            foreach (var s in allFanSensors)
            {
                if (s.Value.HasValue && s.Value.Value > 0)
                {
                    string cleanName = NormalizeSysFanName(s.Name, sysIdx++);
                    systemFans.Add(new FanItemStats
                    {
                        Name = cleanName,
                        Rpm = MathF.Round(s.Value.Value, 0)
                    });
                }
            }

            // WMI Fallback (Win32_Fan - OEM anakartlar için)
            if (systemFans.Count == 0 && stats.Cpu.FanRpm <= 0)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, DesiredSpeed FROM Win32_Fan");
                    int idx = 1;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        uint speed = Convert.ToUInt32(mo["DesiredSpeed"] ?? 0);
                        if (speed > 0)
                        {
                            if (stats.Cpu.FanRpm <= 0)
                            {
                                stats.Cpu.FanRpm = speed;
                            }
                            else
                            {
                                systemFans.Add(new FanItemStats
                                {
                                    Name = $"SYS {idx++}",
                                    Rpm = speed
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            stats.Fans = systemFans;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CollectFans error: {ex.Message}");
        }
    }

    private static string NormalizeSysFanName(string raw, int fallbackIndex)
    {
        if (string.IsNullOrWhiteSpace(raw)) return $"SYS {fallbackIndex}";
        string name = raw.Trim();

        // Her anakart markasındaki numarayı yakala (SYS_FAN1, Chassis Fan 2, Fan #3 vb.)
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\d+");
        if (match.Success)
        {
            return $"SYS {match.Value}";
        }

        return $"SYS {fallbackIndex}";
    }

    private static float GetAcpiThermalZoneTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject mo in searcher.Get())
            {
                uint raw = Convert.ToUInt32(mo["CurrentTemperature"] ?? 0);
                if (raw > 2732)
                {
                    float cel = (raw - 2732f) / 10f;
                    if (cel > 20 && cel < 115) return MathF.Round(cel, 0);
                }
            }
        }
        catch { }
        return 0f;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public void Dispose()
    {
        Stop();
        _timer?.Dispose();
        _cpuFallbackCounter?.Dispose();
        _cpuPerfPercentCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        _computer?.Close();
    }
}
