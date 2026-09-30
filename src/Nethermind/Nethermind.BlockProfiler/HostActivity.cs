// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nethermind.BlockProfiler;

/// <summary>
/// What else the machine did around the counted windows (<c>NETHERMIND_COUNT_DIAG=1</c>): busy time per CPU group from
/// <c>/proc/stat</c>, which a container reads host-wide, and the host settings that move cycles but not instructions.
/// </summary>
internal static class HostActivity
{
    private static long[]? s_lastBusy;
    private static bool[]? s_own;

    /// <summary>
    /// Busy jiffies since the previous call on the pinned CPU, on the rest of the process's CPUs and on every other CPU
    /// (user, nice, system, irq, softirq and steal time; one jiffy is 10 ms).
    /// </summary>
    public static string Delta(int pinCpu)
    {
        long[]? busy = ReadBusy();
        if (busy is null) return "?";
        bool[] own = s_own ??= ReadOwnCpus(busy.Length);
        long[]? last = s_lastBusy;
        s_lastBusy = busy;
        if (last is null || last.Length != busy.Length) return "0/0/0";
        long pin = 0, ours = 0, others = 0;
        for (int cpu = 0; cpu < busy.Length; cpu++)
        {
            long delta = busy[cpu] - last[cpu];
            if (cpu == pinCpu) pin += delta;
            else if (cpu < own.Length && own[cpu]) ours += delta;
            else others += delta;
        }
        return $"{pin}/{ours}/{others}";
    }

    /// <summary>Resident and transparent-huge-page-backed anonymous memory of this process, in kB.</summary>
    public static string Memory()
    {
        long rss = -1, huge = -1;
        try
        {
            foreach (string line in File.ReadLines("/proc/self/smaps_rollup"))
            {
                if (line.StartsWith("Rss:", StringComparison.Ordinal)) rss = ParseKb(line);
                else if (line.StartsWith("AnonHugePages:", StringComparison.Ordinal)) huge = ParseKb(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return $"{rss}/{huge}";

        static long ParseKb(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb) ? kb : -1;
        }
    }

    /// <summary>One line of the host settings that decide caches, pages and idle states, as the container sees them.</summary>
    public static string Facts(int pinCpu)
    {
        StringBuilder text = new();
        Add("thp", "/sys/kernel/mm/transparent_hugepage/enabled");
        Add("thpdefrag", "/sys/kernel/mm/transparent_hugepage/defrag");
        Add("aslr", "/proc/sys/kernel/randomize_va_space");
        Add("nmiwatchdog", "/proc/sys/kernel/nmi_watchdog");
        Add("numabalancing", "/proc/sys/kernel/numa_balancing");
        Add("idledriver", "/sys/devices/system/cpu/cpuidle/current_driver");
        Add("idlegovernor", "/sys/devices/system/cpu/cpuidle/current_governor_ro");
        Add("pstate", "/sys/devices/system/cpu/amd_pstate/status");
        int cpu = pinCpu >= 0 ? pinCpu : 0;
        Add("governor", $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_governor");
        Add("maxfreq", $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_max_freq");
        Add("curfreq", $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_cur_freq");
        Add("epp", $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/energy_performance_preference");
        Add("boost", "/sys/devices/system/cpu/cpufreq/boost");
        for (int state = 0; state < 10; state++)
        {
            string dir = $"/sys/devices/system/cpu/cpu{cpu}/cpuidle/state{state}";
            if (!Directory.Exists(dir)) break;
            text.Append($" idle{state}={Read($"{dir}/name")}:{Read($"{dir}/latency")}us:{(Read($"{dir}/disable") == "1" ? "off" : "on")}");
        }
        text.Append($" cpus={ReadStatus("Cpus_allowed_list")}");
        text.Append($" events={DiagnosticCounters.Describe()}");
        return text.ToString();

        void Add(string name, string path) => text.Append(' ').Append(name).Append('=').Append(Read(path).Replace(' ', ','));
    }

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }

    private static string ReadStatus(string key)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith(key, StringComparison.Ordinal)) return line[(line.IndexOf(':') + 1)..].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return "-";
    }

    private static long[]? ReadBusy()
    {
        try
        {
            List<long> busy = [];
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;
                if (line.Length < 4 || line[3] == ' ') continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int cpu = int.Parse(parts[0].AsSpan(3), CultureInfo.InvariantCulture);
                while (busy.Count <= cpu) busy.Add(0);
                // user nice system idle iowait irq softirq steal
                long Field(int i) => parts.Length > i ? long.Parse(parts[i], CultureInfo.InvariantCulture) : 0;
                busy[cpu] = Field(1) + Field(2) + Field(3) + Field(6) + Field(7) + Field(8);
            }
            return [.. busy];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static bool[] ReadOwnCpus(int count)
    {
        bool[] own = new bool[Math.Max(count, 1)];
        string list = ReadStatus("Cpus_allowed_list");
        foreach (string range in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] bounds = range.Split('-');
            if (!int.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first)) continue;
            int last = bounds.Length > 1 && int.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) ? end : first;
            for (int cpu = first; cpu <= last && cpu < own.Length; cpu++) own[cpu] = true;
        }
        return own;
    }
}
