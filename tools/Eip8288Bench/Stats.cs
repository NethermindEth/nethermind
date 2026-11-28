// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Eip8288Bench;

/// <summary>Summary statistics of one measured quantity across repeats.</summary>
internal sealed record Summary(int Count, double Mean, double StdDev, double Cv, double Min, double P50, double P95, double P99, double Max)
{
    /// <summary>Summarises <paramref name="values"/>; percentiles are nearest-rank, <c>sorted[ceil(p * n) - 1]</c>.</summary>
    /// <remarks>With fewer than twenty samples p95 and p99 are the maximum; they are not tail estimates.</remarks>
    public static Summary Of(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        double[] sorted = [.. values];
        Array.Sort(sorted);
        double mean = 0;
        foreach (double value in sorted) mean += value;
        mean /= sorted.Length;
        double variance = 0;
        foreach (double value in sorted) variance += (value - mean) * (value - mean);
        double stdDev = sorted.Length > 1 ? Math.Sqrt(variance / (sorted.Length - 1)) : 0;
        return new(sorted.Length, mean, stdDev, mean == 0 ? 0 : stdDev / mean, sorted[0], Rank(sorted, 0.50), Rank(sorted, 0.95),
            Rank(sorted, 0.99), sorted[^1]);
    }

    private static double Rank(double[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
}

/// <summary>The machine and limits a run measured, so results only compare across like hosts.</summary>
internal static class HostInfo
{
    public static Dictionary<string, object?> Capture()
    {
        Dictionary<string, object?> info = new()
        {
            ["os"] = Environment.OSVersion.ToString(),
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["cpuModel"] = CpuModel(),
            ["memTotalKiB"] = MemInfo("MemTotal"),
            ["cgroupCpuMax"] = ReadTrimmed("/sys/fs/cgroup/cpu.max"),
            ["cgroupCpusetEffective"] = ReadTrimmed("/sys/fs/cgroup/cpuset.cpus.effective"),
            ["cgroupMemoryMax"] = ReadTrimmed("/sys/fs/cgroup/memory.max"),
            ["kernel"] = ReadTrimmed("/proc/sys/kernel/osrelease"),
            ["leanvmNumThreads"] = Environment.GetEnvironmentVariable("LEANVM_NUM_THREADS"),
            ["machineType"] = Environment.GetEnvironmentVariable("BENCH_MACHINE_TYPE"),
        };
        return info;
    }

    private static string? CpuModel()
    {
        if (!File.Exists("/proc/cpuinfo")) return null;
        foreach (string line in File.ReadLines("/proc/cpuinfo"))
            if (line.StartsWith("model name", StringComparison.Ordinal)) return line[(line.IndexOf(':') + 1)..].Trim();
        return null;
    }

    public static long? MemInfo(string key)
    {
        if (!File.Exists("/proc/meminfo")) return null;
        foreach (string line in File.ReadLines("/proc/meminfo"))
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return null;
    }

    public static string? ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The process's peak resident set, in KiB, since start or the last <see cref="ResetPeakRss"/>.</summary>
    public static long PeakRssKiB() => ProcStatus("VmHWM");

    public static long RssKiB() => ProcStatus("VmRSS");

    /// <summary>Resets <c>VmHWM</c> to the current resident set (Linux <c>clear_refs</c> value 5), so each repeat reports its own peak.</summary>
    public static bool ResetPeakRss()
    {
        try
        {
            File.WriteAllText("/proc/self/clear_refs", "5");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static long ProcStatus(string key)
    {
        if (!File.Exists("/proc/self/status")) return 0;
        foreach (string line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return 0;
    }
}
