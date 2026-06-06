using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

/// <summary>
/// Windows system monitor. All counter failures are non-fatal;
/// individual metrics return 0 when unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
public class SystemMonitorService : ISystemMonitorService
{
    private readonly PerformanceCounter? _cpuCounter;
    private readonly PerformanceCounter? _ramCounter;
    private readonly PerformanceCounter? _diskReadCounter;
    private readonly PerformanceCounter? _diskWriteCounter;
    private readonly PerformanceCounter? _netSentCounter;
    private readonly PerformanceCounter? _netRecvCounter;
    private readonly long _totalRamBytes;
    private DateTime _lastSampleTime;

    public SystemMonitorService()
    {
        _cpuCounter = SafeCreateCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = SafeCreateCounter("Memory", "Available MBytes");

        _diskReadCounter = SafeCreateCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _diskWriteCounter = SafeCreateCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");

        var netIface = SafeGetNetInterface();
        _netSentCounter = SafeCreateCounter("Network Interface", "Bytes Sent/sec", netIface);
        _netRecvCounter = SafeCreateCounter("Network Interface", "Bytes Received/sec", netIface);

        try
        {
            var memInfo = new MEMORYSTATUSEX();
            memInfo.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref memInfo))
                _totalRamBytes = (long)memInfo.ullTotalPhys;
        }
        catch { _totalRamBytes = 0; }

        SafeNextValue(_cpuCounter);
        SafeNextValue(_ramCounter);
        SafeNextValue(_diskReadCounter);
        SafeNextValue(_diskWriteCounter);
        SafeNextValue(_netSentCounter);
        SafeNextValue(_netRecvCounter);
        _lastSampleTime = DateTime.UtcNow;
    }

    public SystemMetrics GetCurrentMetrics()
    {
        var now = DateTime.UtcNow;
        var cpu = SafeNextValue(_cpuCounter);
        var availMb = SafeNextValue(_ramCounter);
        var diskRead = SafeNextValue(_diskReadCounter);
        var diskWrite = SafeNextValue(_diskWriteCounter);
        var netSent = SafeNextValue(_netSentCounter);
        var netRecv = SafeNextValue(_netRecvCounter);

        var totalRamMb = _totalRamBytes / (1024.0 * 1024.0);
        var usedRamMb = totalRamMb - availMb;

        _lastSampleTime = now;

        return new SystemMetrics
        {
            CpuPercent = Math.Round(cpu, 1),
            RamUsedGb = Math.Round(usedRamMb / 1024.0, 2),
            RamTotalGb = Math.Round(totalRamMb / 1024.0, 2),
            DiskReadMbps = Math.Round(diskRead / (1024.0 * 1024.0), 2),
            DiskWriteMbps = Math.Round(diskWrite / (1024.0 * 1024.0), 2),
            NetworkDownMbps = Math.Round(netRecv * 8 / 1_000_000.0, 2),
            NetworkUpMbps = Math.Round(netSent * 8 / 1_000_000.0, 2),
            GpuPercent = SafeGetGpuUsage(),
            ProcessCount = SafeProcessCount(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Timestamp = now
        };
    }

    public async IAsyncEnumerable<SystemMetrics> StreamMetricsAsync(
        TimeSpan interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return GetCurrentMetrics();
            await Task.Delay(interval, ct);
        }
    }

    private static PerformanceCounter? SafeCreateCounter(string category, string counter, string? instance = null)
    {
        try
        {
            return instance is null
                ? new PerformanceCounter(category, counter)
                : new PerformanceCounter(category, counter, instance);
        }
        catch { return null; }
    }

    private static float SafeNextValue(PerformanceCounter? counter)
    {
        try { return counter?.NextValue() ?? 0f; }
        catch { return 0f; }
    }

    private static double SafeGetGpuUsage()
    {
        try
        {
            using var gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", "engtype_3D");
            return Math.Round(gpuCounter.NextValue(), 1);
        }
        catch { return 0; }
    }

    private static int SafeProcessCount()
    {
        try { return Process.GetProcesses().Length; }
        catch { return 0; }
    }

    private static string SafeGetNetInterface()
    {
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            var names = category.GetInstanceNames();
            foreach (var name in names)
            {
                var lower = name.ToLower();
                if (lower.Contains("ethernet") || lower.Contains("wi-fi") || lower.Contains("wireless"))
                    return name;
            }
            return names.FirstOrDefault(n =>
                !n.ToLower().Contains("isatap") &&
                !n.ToLower().Contains("teredo") &&
                !n.ToLower().Contains("loopback")) ?? "Ethernet";
        }
        catch { return "Ethernet"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
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
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
