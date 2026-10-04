// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>
/// Experiment: lowers the scheduling priority of thread-pool workers and RocksDB background threads, found by name in
/// /proc/self/task, so the block-processing thread wins its CPU back at once. Linux only; a raised nice cannot be
/// lowered again without CAP_SYS_NICE, so a niced thread stays niced.
/// </summary>
internal static class ThreadNicer
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, int who, int prio);

    internal static void Start(int poolNice, int rocksNice, int reportSeconds)
    {
        if (!OperatingSystem.IsLinux() || (poolNice <= 0 && rocksNice <= 0 && reportSeconds <= 0)) return;
        Thread nicer = new(() => Run(poolNice, rocksNice, reportSeconds)) { IsBackground = true, Name = "Thread nicer" };
        nicer.Start();
    }

    /// <summary>
    /// Prints CPU and run-queue wait per thread name since the previous report: comm, threads, nice values seen,
    /// on-CPU ms and run-queue wait ms (from /proc/self/task/*/schedstat).
    /// </summary>
    private static void Report(Dictionary<int, (long Run, long Wait)> previous)
    {
        Dictionary<string, (int Threads, long Run, long Wait, string Nice)> byName = [];
        Dictionary<int, (long Run, long Wait)> current = [];
        foreach (string dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try
            {
                if (!int.TryParse(Path.GetFileName(dir), out int tid)) continue;
                string comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd((char)10);
                string[] sched = File.ReadAllText(Path.Combine(dir, "schedstat")).Split(' ');
                long run = long.Parse(sched[0]), wait = long.Parse(sched[1]);
                string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                // Fields after the parenthesised comm: state is field 3, nice is field 19.
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                string nice = fields.Length > 16 ? fields[16] : "?";
                current[tid] = (run, wait);
                (long Run, long Wait) before = previous.TryGetValue(tid, out (long Run, long Wait) p) ? p : (0, 0);
                long dRun = run - before.Run, dWait = wait - before.Wait;
                if (dRun == 0 && dWait == 0) continue;
                string key = comm.StartsWith(".NET TP Worker", StringComparison.Ordinal) ? ".NET TP Worker" : comm;
                (int Threads, long Run, long Wait, string Nice) agg = byName.TryGetValue(key, out (int Threads, long Run, long Wait, string Nice) a) ? a : (0, 0, 0, "");
                byName[key] = (agg.Threads + 1, agg.Run + dRun, agg.Wait + dWait, agg.Nice.Contains("|" + nice + "|") ? agg.Nice : agg.Nice + "|" + nice + "|");
            }
            catch (Exception)
            {
                // A thread that exits mid-read.
            }
        }

        previous.Clear();
        foreach (KeyValuePair<int, (long Run, long Wait)> kv in current) previous[kv.Key] = kv.Value;
        System.Text.StringBuilder line = new("EXP-THREADS");
        foreach (KeyValuePair<string, (int Threads, long Run, long Wait, string Nice)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(byName, static kv => kv.Value.Run), 25))
            line.Append(' ').Append(kv.Key.Replace(' ', '_')).Append(":t").Append(kv.Value.Threads).Append(":run").Append(kv.Value.Run / 1_000_000)
                .Append(":wait").Append(kv.Value.Wait / 1_000_000).Append(":n").Append(kv.Value.Nice.Replace("||", ",").Trim('|'));
        Console.Out.WriteLine(line.ToString());
    }

    private static void Run(int poolNice, int rocksNice, int reportSeconds)
    {
        Dictionary<int, (long Run, long Wait)> reported = [];
        long nextReport = Environment.TickCount64 + reportSeconds * 1000L;
        HashSet<int> done = [];
        Dictionary<int, int> unnamed = [];
        int pool = 0, rocks = 0, failed = 0;
        for (long sweep = 0; ; sweep++)
        {
            try
            {
                foreach (string dir in Directory.EnumerateDirectories("/proc/self/task"))
                {
                    if (!int.TryParse(Path.GetFileName(dir), out int tid) || done.Contains(tid)) continue;
                    string comm;
                    try
                    {
                        comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n');
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    bool isPool = comm.StartsWith(".NET TP Worker", StringComparison.Ordinal);
                    bool isRocks = comm.StartsWith("rocksdb:", StringComparison.Ordinal);
                    if (!isPool && !isRocks)
                    {
                        // A new thread may not have its name yet; give it a second before settling on leaving it alone.
                        int seen = unnamed.TryGetValue(tid, out int n) ? n + 1 : 1;
                        if (seen >= 20 || comm.StartsWith('.')) { done.Add(tid); unnamed.Remove(tid); }
                        else unnamed[tid] = seen;
                        continue;
                    }

                    int nice = isPool ? poolNice : rocksNice;
                    if (nice > 0 && (poolNice > 0 || rocksNice > 0))
                    {
                        if (setpriority(0, tid, nice) == 0)
                        {
                            if (isPool) pool++;
                            else rocks++;
                        }
                        else failed++;
                    }

                    done.Add(tid);
                    unnamed.Remove(tid);
                }
            }
            catch (Exception)
            {
                // A thread that exits mid-sweep: the next sweep goes on.
            }

            if (sweep % 1200 == 1 && (poolNice > 0 || rocksNice > 0)) Console.Out.WriteLine($"EXP-NICE pool={pool} rocks={rocks} failed={failed} sweeps={sweep}");
            if (reportSeconds > 0 && Environment.TickCount64 >= nextReport)
            {
                nextReport = Environment.TickCount64 + reportSeconds * 1000L;
                try
                {
                    Report(reported);
                }
                catch (Exception)
                {
                    // Diagnostics only.
                }
            }

            Thread.Sleep(50);
        }
    }
}
