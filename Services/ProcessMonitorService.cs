using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MacroDeck.Server.Models;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace MacroDeck.Server.Services;

public class ProcessMonitorService : IDisposable
{
    private static readonly Lazy<ProcessMonitorService> _instance = new(() => new ProcessMonitorService());
    public static ProcessMonitorService Instance => _instance.Value;

    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private bool _isRunning = false;

    // Hedef işlem (0 veya -1 ise otomatik aktif ön plan penceresi)
    private int _targetPid = 0;
    private string _targetExeName = "";
    private string _lastTrackedKey = "";

    // CPU takibi için önceki zaman damgaları
    private DateTime _lastCpuSampleTime = DateTime.UtcNow;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;

    // Disk I/O takibi için önceki sayaçlar
    private DateTime _lastIoSampleTime = DateTime.UtcNow;
    private ulong _lastReadBytes = 0;
    private ulong _lastWriteBytes = 0;

    // Ağ / Network takibi için ETW sayaçları
    private TraceEventSession? _etwSession;
    private Thread? _etwThread;
    private long _netBytesRecv = 0;
    private long _netBytesSend = 0;
    private long _lastNetBytesRecv = 0;
    private long _lastNetBytesSend = 0;
    private DateTime _lastNetSampleTime = DateTime.UtcNow;
    private readonly HashSet<int> _monitoredPids = new();
    private readonly object _pidLock = new();

    // GPU takibi için PerformanceCounter dinamik sözlüğü (instanceName -> PerformanceCounter)
    private readonly Dictionary<string, PerformanceCounter> _activeGpuCounters = new();
    private DateTime _lastGpuDiscoveryTime = DateTime.MinValue;

    // 10 dakikalık geçmiş kuyrukları (1 sn aralıkla maksimum 600 nokta)
    private const int MaxHistoryPoints = 600;
    private readonly Queue<float> _cpuHistory = new();
    private readonly Queue<float> _ramHistory = new();
    private readonly Queue<float> _gpuHistory = new();
    private readonly Queue<float> _diskHistory = new();
    private readonly Queue<float> _netHistory = new();

    // Toplam sistem belleği (RAM yüzdesi hesabı için)
    private ulong _totalSystemRamBytes = 32UL * 1024 * 1024 * 1024; // 32 GB varsayılan

    public event Action<ProcessStats>? StatsUpdated;

    #region Win32 API
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr ProcessHandle, out IO_COUNTERS IoCounters);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    #endregion

    public ProcessMonitorService()
    {
        try
        {
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem) && mem.ullTotalPhys > 0)
            {
                _totalSystemRamBytes = mem.ullTotalPhys;
            }
        }
        catch { }

        // Başlangıç kuyruklarını sıfırla doldur
        lock (_lock)
        {
            for (int i = 0; i < 60; i++)
            {
                _cpuHistory.Enqueue(0);
                _ramHistory.Enqueue(0);
                _gpuHistory.Enqueue(0);
                _diskHistory.Enqueue(0);
                _netHistory.Enqueue(0);
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _isRunning = true;
            StartEtwSession();
            _timer ??= new System.Threading.Timer(OnTimerTick, null, 0, 1000);
            _timer.Change(0, 1000);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            StopEtwSession();
            CleanupGpuCounters();
        }
    }

    public void SetTargetProcess(int pid, string? exeName = null)
    {
        lock (_lock)
        {
            string cleanExe = (exeName ?? "").Trim();
            if (cleanExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                cleanExe = cleanExe.Substring(0, cleanExe.Length - 4);
            }

            if (_targetPid != pid || !string.Equals(_targetExeName, cleanExe, StringComparison.OrdinalIgnoreCase))
            {
                _targetPid = pid;
                _targetExeName = cleanExe;
                ResetTracking();
            }
        }
    }

    public void SetTargetPid(int pid)
    {
        SetTargetProcess(pid, null);
    }

    private void ResetTracking()
    {
        _lastTrackedKey = "";
        _lastCpuSampleTime = DateTime.UtcNow;
        _lastTotalProcessorTime = TimeSpan.Zero;
        _lastIoSampleTime = DateTime.UtcNow;
        _lastReadBytes = 0;
        _lastWriteBytes = 0;
        _lastNetSampleTime = DateTime.UtcNow;
        _lastNetBytesRecv = 0;
        _lastNetBytesSend = 0;
        Interlocked.Exchange(ref _netBytesRecv, 0);
        Interlocked.Exchange(ref _netBytesSend, 0);
        CleanupGpuCounters();
    }

    private void CleanupGpuCounters()
    {
        lock (_activeGpuCounters)
        {
            foreach (var c in _activeGpuCounters.Values)
            {
                try { c.Dispose(); } catch { }
            }
            _activeGpuCounters.Clear();
        }
    }

    #region ETW Kernel Network Listener
    private void StartEtwSession()
    {
        if (_etwSession != null) return;

        try
        {
            // Eski artık oturum varsa temizle
            try { TraceEventSession.GetActiveSession("MacroDeckNetSession")?.Stop(); } catch { }

            _etwSession = new TraceEventSession("MacroDeckNetSession");
            _etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            _etwSession.Source.Kernel.TcpIpRecv += data =>
            {
                lock (_pidLock)
                {
                    if (_monitoredPids.Contains(data.ProcessID))
                    {
                        Interlocked.Add(ref _netBytesRecv, data.size);
                    }
                }
            };

            _etwSession.Source.Kernel.TcpIpSend += data =>
            {
                lock (_pidLock)
                {
                    if (_monitoredPids.Contains(data.ProcessID))
                    {
                        Interlocked.Add(ref _netBytesSend, data.size);
                    }
                }
            };

            _etwSession.Source.Kernel.UdpIpRecv += data =>
            {
                lock (_pidLock)
                {
                    if (_monitoredPids.Contains(data.ProcessID))
                    {
                        Interlocked.Add(ref _netBytesRecv, data.size);
                    }
                }
            };

            _etwSession.Source.Kernel.UdpIpSend += data =>
            {
                lock (_pidLock)
                {
                    if (_monitoredPids.Contains(data.ProcessID))
                    {
                        Interlocked.Add(ref _netBytesSend, data.size);
                    }
                }
            };

            _etwThread = new Thread(() =>
            {
                try
                {
                    _etwSession.Source.Process();
                }
                catch { }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _etwThread.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ETW Start failed: {ex.Message}");
            _etwSession = null;
        }
    }

    private void StopEtwSession()
    {
        try
        {
            _etwSession?.Stop();
            _etwSession?.Dispose();
        }
        catch { }
        _etwSession = null;
        _etwThread = null;
    }
    #endregion

    private void OnTimerTick(object? state)
    {
        if (!_isRunning) return;

        try
        {
            var stats = CollectProcessStats();
            StatsUpdated?.Invoke(stats);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessMonitor error: {ex.Message}");
        }
    }

    public ProcessStats CollectProcessStats()
    {
        var stats = new ProcessStats();
        int activePid = _targetPid;
        string targetExe = _targetExeName;
        bool isAuto = activePid <= 0 && string.IsNullOrWhiteSpace(targetExe);

        IntPtr fgHwnd = IntPtr.Zero;
        string windowTitle = "";

        // 1. Hedef Belirleme:
        // A) Belirli bir exe adı istenmişse
        if (!string.IsNullOrWhiteSpace(targetExe))
        {
            // Hedef uygulama adı doğrudan targetExe
        }
        // B) Belirli bir PID seçilmişse
        else if (activePid > 0)
        {
            try
            {
                var p = Process.GetProcessById(activePid);
                targetExe = p.ProcessName;
                try { windowTitle = p.MainWindowTitle; } catch { }
            }
            catch
            {
                targetExe = "";
            }
        }
        // C) Otomatik aktif ön plan penceresi
        else if (isAuto)
        {
            try
            {
                fgHwnd = GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fgHwnd, out uint pid);
                    activePid = (int)pid;

                    var sb = new StringBuilder(256);
                    if (GetWindowText(fgHwnd, sb, 256) > 0)
                    {
                        windowTitle = sb.ToString();
                    }

                    var p = Process.GetProcessById(activePid);
                    targetExe = p.ProcessName;
                }
            }
            catch { }
        }

        stats.IsAutoActiveWindow = isAuto;

        if (string.IsNullOrWhiteSpace(targetExe))
        {
            stats.Pid = 0;
            stats.Name = "Bekleniyor...";
            stats.Title = "Aktif pencere yok";
            UpdateHistories(stats);
            return stats;
        }

        // 2. Çoklu İşlem Gruplama (Task Manager / Görev Yöneticisi Gibi)
        // Chrome, Edge, Discord, Steam ve oyunlar gibi uygulamalar birden fazla işlem çalıştırır.
        // Task Manager bunların tamamının toplam RAM, CPU, GPU ve Ağ kullanımını raporlar.
        var procs = Process.GetProcessesByName(targetExe);
        if (procs.Length == 0)
        {
            stats.Pid = 0;
            stats.Name = targetExe + ".exe";
            stats.Title = "Uygulama çalışmıyor (Başlatılması bekleniyor...)";
            UpdateHistories(stats);
            return stats;
        }

        // Ana penceresi olan veya ilk işlemi tespit et
        var mainProc = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? procs[0];
        stats.Pid = mainProc.Id;
        stats.Name = targetExe + ".exe";

        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            try { windowTitle = mainProc.MainWindowTitle; } catch { }
        }
        string countSuffix = procs.Length > 1 ? $" ({procs.Length} İşlem)" : "";
        stats.Title = (string.IsNullOrWhiteSpace(windowTitle) ? stats.Name : windowTitle) + countSuffix;

        // Hedef uygulama değiştiyse sayaçları sıfırla
        string currentKey = targetExe.ToLowerInvariant();
        if (_lastTrackedKey != currentKey)
        {
            ResetTracking();
            _lastTrackedKey = currentKey;
        }

        // İzlenen PID listesini güncelle (Ağ ve GPU için)
        var currentPids = procs.Select(p => p.Id).ToHashSet();
        lock (_pidLock)
        {
            _monitoredPids.Clear();
            foreach (var p in currentPids) _monitoredPids.Add(p);
        }

        DateTime now = DateTime.UtcNow;

        // 3. RAM Tüketimi (Uygulamanın tüm işlemlerinin toplam WorkingSet64 değeri)
        try
        {
            long totalRamBytes = 0;
            foreach (var p in procs)
            {
                try
                {
                    p.Refresh();
                    totalRamBytes += p.WorkingSet64;
                }
                catch { }
            }

            stats.RamMb = MathF.Round(totalRamBytes / (1024f * 1024f), 1);
            if (_totalSystemRamBytes > 0)
            {
                stats.RamPercent = MathF.Round((float)totalRamBytes / _totalSystemRamBytes * 100f, 1);
            }
        }
        catch { }

        // 4. CPU Tüketim Yüzdesi (Tüm alt işlemlerin toplam CPU zamanı farkı)
        try
        {
            TimeSpan totalCpuTime = TimeSpan.Zero;
            foreach (var p in procs)
            {
                try { totalCpuTime += p.TotalProcessorTime; } catch { }
            }

            double timeDeltaMs = (now - _lastCpuSampleTime).TotalMilliseconds;
            if (_lastTotalProcessorTime != TimeSpan.Zero && timeDeltaMs > 100)
            {
                double procDeltaMs = (totalCpuTime - _lastTotalProcessorTime).TotalMilliseconds;
                double cpuUsage = (procDeltaMs / (timeDeltaMs * Environment.ProcessorCount)) * 100.0;
                stats.CpuPercent = MathF.Round(Math.Clamp((float)cpuUsage, 0f, 100f), 1);
            }

            _lastCpuSampleTime = now;
            _lastTotalProcessorTime = totalCpuTime;
        }
        catch { }

        // 5. Disk I/O Kullanımı (GetProcessIoCounters - Okuma/Yazma toplamı)
        try
        {
            ulong totalRead = 0;
            ulong totalWrite = 0;

            foreach (var p in procs)
            {
                try
                {
                    if (GetProcessIoCounters(p.Handle, out IO_COUNTERS counters))
                    {
                        totalRead += counters.ReadTransferCount;
                        totalWrite += counters.WriteTransferCount;
                    }
                }
                catch { }
            }

            double elapsedSec = (now - _lastIoSampleTime).TotalSeconds;
            if (elapsedSec > 0.1 && (_lastReadBytes > 0 || _lastWriteBytes > 0))
            {
                ulong readDiff = totalRead > _lastReadBytes ? totalRead - _lastReadBytes : 0;
                ulong writeDiff = totalWrite > _lastWriteBytes ? totalWrite - _lastWriteBytes : 0;

                stats.DiskReadMb = MathF.Round((float)readDiff / (1024f * 1024f) / (float)elapsedSec, 2);
                stats.DiskWriteMb = MathF.Round((float)writeDiff / (1024f * 1024f) / (float)elapsedSec, 2);

                // 150 MB/s disk referansına göre yüzde
                float totalDiskMb = stats.DiskReadMb + stats.DiskWriteMb;
                stats.DiskPercent = MathF.Round(Math.Clamp((totalDiskMb / 150f) * 100f, 0f, 100f), 1);
            }

            _lastReadBytes = totalRead;
            _lastWriteBytes = totalWrite;
            _lastIoSampleTime = now;
        }
        catch { }

        // 6. Ağ / Network Kullanımı (ETW Kernel Network Tracing)
        try
        {
            long currentRecv = Interlocked.Read(ref _netBytesRecv);
            long currentSend = Interlocked.Read(ref _netBytesSend);
            double netElapsedSec = (now - _lastNetSampleTime).TotalSeconds;

            if (netElapsedSec > 0.1 && (_lastNetBytesRecv > 0 || _lastNetBytesSend > 0))
            {
                long recvDiff = currentRecv > _lastNetBytesRecv ? currentRecv - _lastNetBytesRecv : 0;
                long sendDiff = currentSend > _lastNetBytesSend ? currentSend - _lastNetBytesSend : 0;

                stats.NetRecvMb = MathF.Round((float)recvDiff / (1024f * 1024f) / (float)netElapsedSec, 2);
                stats.NetSendMb = MathF.Round((float)sendDiff / (1024f * 1024f) / (float)netElapsedSec, 2);

                float totalNetMb = stats.NetRecvMb + stats.NetSendMb;
                // 100 Mbps (12.5 MB/s) internet hattına göre kullanım yüzdesi
                stats.NetPercent = MathF.Round(Math.Clamp((totalNetMb / 12.5f) * 100f, 0f, 100f), 1);
            }

            _lastNetBytesRecv = currentRecv;
            _lastNetBytesSend = currentSend;
            _lastNetSampleTime = now;
        }
        catch { }

        // 7. GPU Tüketimi (Windows GPU Engine Performance Counters - Dinamik Güncelleme)
        try
        {
            stats.GpuPercent = ReadGpuUsage(currentPids);
        }
        catch { }

        UpdateHistories(stats);
        return stats;
    }

    private float ReadGpuUsage(HashSet<int> pids)
    {
        try
        {
            // Her 2 saniyede bir veya sayaç boşsa GPU Engine instance'larını tara
            if ((DateTime.UtcNow - _lastGpuDiscoveryTime).TotalSeconds >= 2 || _activeGpuCounters.Count == 0)
            {
                _lastGpuDiscoveryTime = DateTime.UtcNow;
                var cat = new PerformanceCounterCategory("GPU Engine");
                string[] instances = cat.GetInstanceNames();
                var relevant = new HashSet<string>();

                foreach (var inst in instances)
                {
                    // inst: pid_XXXX_luid_...
                    int first = inst.IndexOf('_');
                    if (first >= 0)
                    {
                        int second = inst.IndexOf('_', first + 1);
                        if (second > first)
                        {
                            string pidStr = inst.Substring(first + 1, second - first - 1);
                            if (int.TryParse(pidStr, out int p) && pids.Contains(p))
                            {
                                relevant.Add(inst);
                            }
                        }
                    }
                }

                lock (_activeGpuCounters)
                {
                    // Artık aktif olmayan sayaçları temizle
                    var dead = _activeGpuCounters.Keys.Where(k => !relevant.Contains(k)).ToList();
                    foreach (var d in dead)
                    {
                        try { _activeGpuCounters[d].Dispose(); } catch { }
                        _activeGpuCounters.Remove(d);
                    }

                    // Yeni sayaçları ekle
                    foreach (var r in relevant)
                    {
                        if (!_activeGpuCounters.ContainsKey(r))
                        {
                            try
                            {
                                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", r, true);
                                c.NextValue(); // İlk okuma sayacı başlatır
                                _activeGpuCounters[r] = c;
                            }
                            catch { }
                        }
                    }
                }
            }

            // Aktif sayaçların toplamını al
            float totalGpu = 0f;
            lock (_activeGpuCounters)
            {
                foreach (var c in _activeGpuCounters.Values)
                {
                    try
                    {
                        totalGpu += c.NextValue();
                    }
                    catch { }
                }
            }

            return MathF.Round(Math.Clamp(totalGpu, 0f, 100f), 1);
        }
        catch { }

        return 0f;
    }

    private void UpdateHistories(ProcessStats stats)
    {
        lock (_lock)
        {
            _cpuHistory.Enqueue(stats.CpuPercent);
            while (_cpuHistory.Count > MaxHistoryPoints) _cpuHistory.Dequeue();
            stats.CpuHistory = _cpuHistory.ToList();

            _ramHistory.Enqueue(stats.RamPercent);
            while (_ramHistory.Count > MaxHistoryPoints) _ramHistory.Dequeue();
            stats.RamHistory = _ramHistory.ToList();

            _gpuHistory.Enqueue(stats.GpuPercent);
            while (_gpuHistory.Count > MaxHistoryPoints) _gpuHistory.Dequeue();
            stats.GpuHistory = _gpuHistory.ToList();

            _diskHistory.Enqueue(stats.DiskPercent);
            while (_diskHistory.Count > MaxHistoryPoints) _diskHistory.Dequeue();
            stats.DiskHistory = _diskHistory.ToList();

            _netHistory.Enqueue(stats.NetPercent);
            while (_netHistory.Count > MaxHistoryPoints) _netHistory.Dequeue();
            stats.NetHistory = _netHistory.ToList();
        }
    }

    public List<ProcessItemInfo> GetRunningProcesses()
    {
        var list = new List<ProcessItemInfo>();
        try
        {
            var processes = Process.GetProcesses();

            // Task Manager gibi ProcessName bazında grupla
            var groups = processes
                .Where(p => p.Id > 4 &&
                            !string.Equals(p.ProcessName, "Idle", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(p.ProcessName, "System", StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in groups)
            {
                try
                {
                    string procName = grp.Key;
                    string bestTitle = "";
                    long totalRam = 0;
                    int bestPid = 0;

                    foreach (var p in grp)
                    {
                        try
                        {
                            totalRam += p.WorkingSet64;
                            if (string.IsNullOrWhiteSpace(bestTitle) && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                            {
                                bestTitle = p.MainWindowTitle;
                                bestPid = p.Id;
                            }
                        }
                        catch { }
                    }

                    if (bestPid == 0)
                    {
                        bestPid = grp.First().Id;
                    }

                    float ramMb = MathF.Round(totalRam / (1024f * 1024f), 1);

                    // Kullanıcı arayüzü olan veya RAM kullanımı > 10MB olan tüm işlemleri dahil et
                    if (!string.IsNullOrWhiteSpace(bestTitle) || ramMb > 10f)
                    {
                        string exeName = procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? procName : procName + ".exe";
                        string countSuffix = grp.Count() > 1 ? $" ({grp.Count()})" : "";

                        list.Add(new ProcessItemInfo
                        {
                            Pid = bestPid,
                            Name = exeName,
                            Title = (string.IsNullOrWhiteSpace(bestTitle) ? exeName : bestTitle) + countSuffix,
                            RamMb = ramMb
                        });
                    }
                }
                catch { }
            }
        }
        catch { }

        // Öncelik: Penceresi olanlar ve RAM kullanımına göre sıralı, ilk 100 işlem
        return list
            .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.Title) && p.Title != p.Name)
            .ThenByDescending(p => p.RamMb)
            .Take(100)
            .ToList();
    }

    public void Dispose()
    {
        Stop();
        _timer?.Dispose();
    }
}
