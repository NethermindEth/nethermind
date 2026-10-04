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

    internal static void Start(int poolNice, int rocksNice)
    {
        if (!OperatingSystem.IsLinux() || (poolNice <= 0 && rocksNice <= 0)) return;
        Thread nicer = new(() => Run(poolNice, rocksNice)) { IsBackground = true, Name = "Thread nicer" };
        nicer.Start();
    }

    private static void Run(int poolNice, int rocksNice)
    {
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
                    if (nice > 0)
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

            if (sweep % 1200 == 1) Console.Out.WriteLine($"EXP-NICE pool={pool} rocks={rocks} failed={failed} sweeps={sweep}");
            Thread.Sleep(50);
        }
    }
}
