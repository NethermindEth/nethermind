// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Nethermind.Db;

/// <summary>
/// Measurement probe (not for merge): breaks down processing-thread flat reads that miss every managed cache.
/// </summary>
public static class ColdReadProbe
{
    public const int KindAccount = 0;
    public const int KindSlot = 1;

    private const double SlowNativeMicros = 10;
    private static readonly double[] BucketsMicros = [2, 5, 10, 20, 50, 100, 200, 500, 1000, double.MaxValue];
    private static readonly double TicksPerMicro = Stopwatch.Frequency / 1_000_000.0;

    [ThreadStatic] private static bool _armed;
    [ThreadStatic] private static long _armedNativeTicks;
    [ThreadStatic] private static int _armedGets;
    [ThreadStatic] private static IntPtr _perfContext;
    [ThreadStatic] private static bool _perfInitTried;
    private static volatile bool _perfAvailable = false;

    private static readonly Lock _lock = new();
    private static Stats _window = new();
    private static readonly Stats _total = new();
    private static int _windows;

    private sealed class Stats
    {
        public readonly long[] Count = new long[2];
        public readonly long[] Found = new long[2];
        public readonly long[] OuterTicks = new long[2];
        public readonly long[] NativeTicks = new long[2];
        public readonly long[] Gets = new long[2];
        public readonly long[] BucketCount = new long[BucketsMicros.Length];
        public readonly long[] BucketTicks = new long[BucketsMicros.Length];
        public long SlowGets;
        public long FastGets;
        public long OtherNativeGets;
        public long OtherNativeTicks;
        public readonly Dictionary<string, long> SlowPerf = [];
        public readonly Dictionary<string, long> FastPerf = [];
        public long WorstTicks;
        public string? WorstReport;

        public void Add(Stats o)
        {
            for (int i = 0; i < 2; i++)
            {
                Count[i] += o.Count[i]; Found[i] += o.Found[i]; OuterTicks[i] += o.OuterTicks[i];
                NativeTicks[i] += o.NativeTicks[i]; Gets[i] += o.Gets[i];
            }
            for (int i = 0; i < BucketCount.Length; i++) { BucketCount[i] += o.BucketCount[i]; BucketTicks[i] += o.BucketTicks[i]; }
            SlowGets += o.SlowGets; FastGets += o.FastGets;
            OtherNativeGets += o.OtherNativeGets; OtherNativeTicks += o.OtherNativeTicks;
            foreach (KeyValuePair<string, long> kv in o.SlowPerf) SlowPerf[kv.Key] = SlowPerf.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (KeyValuePair<string, long> kv in o.FastPerf) FastPerf[kv.Key] = FastPerf.GetValueOrDefault(kv.Key) + kv.Value;
        }
    }

    public static bool IsArmed => _armed;

    public static void Arm()
    {
        _armed = true;
        _armedNativeTicks = 0;
        _armedGets = 0;
    }

    public static void Disarm(int kind, long outerTicks, bool found)
    {
        _armed = false;
        lock (_lock)
        {
            _window.Count[kind]++;
            if (found) _window.Found[kind]++;
            _window.OuterTicks[kind] += outerTicks;
            _window.NativeTicks[kind] += _armedNativeTicks;
            _window.Gets[kind] += _armedGets;
        }
    }

    /// <summary>Called right before the native Get of an armed read.</summary>
    public static void BeforeNative()
    {
        if (!_perfAvailable) return;
        try
        {
            if (!_perfInitTried)
            {
                _perfInitTried = true;
                // kEnableTimeAndCPUTimeExceptForMutex (or kEnableTime on older enums): thread-local.
                rocksdb_set_perf_level(5);
                _perfContext = rocksdb_perfcontext_create();
            }
            if (_perfContext != IntPtr.Zero) rocksdb_perfcontext_reset(_perfContext);
        }
        catch (Exception e)
        {
            _perfAvailable = false;
            Console.WriteLine($"ColdReadProbe perf context unavailable: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>Called right after the native Get of an armed read.</summary>
    public static void AfterNative(long ticks)
    {
        _armedNativeTicks += ticks;
        _armedGets++;

        string? report = null;
        if (_perfAvailable && _perfContext != IntPtr.Zero)
        {
            try
            {
                IntPtr ptr = rocksdb_perfcontext_report(_perfContext, 1);
                report = Marshal.PtrToStringUTF8(ptr);
                rocksdb_free(ptr);
            }
            catch (Exception e)
            {
                _perfAvailable = false;
                Console.WriteLine($"ColdReadProbe perf report failed: {e.GetType().Name} {e.Message}");
            }
        }

        double micros = ticks / TicksPerMicro;
        bool slow = micros >= SlowNativeMicros;
        lock (_lock)
        {
            int b = 0;
            while (micros >= BucketsMicros[b]) b++;
            _window.BucketCount[b]++;
            _window.BucketTicks[b] += ticks;
            if (slow) _window.SlowGets++; else _window.FastGets++;
            if (report is not null) Accumulate(slow ? _window.SlowPerf : _window.FastPerf, report);
            if (ticks > _window.WorstTicks)
            {
                _window.WorstTicks = ticks;
                _window.WorstReport = report;
            }
        }
    }

    /// <summary>Native Get time of processing-thread reads outside an armed window (trie, metadata, etc.).</summary>
    public static void OtherNative(long ticks)
    {
        lock (_lock)
        {
            _window.OtherNativeGets++;
            _window.OtherNativeTicks += ticks;
        }
    }

    private static void Accumulate(Dictionary<string, long> into, string report)
    {
        foreach (string part in report.Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string value = part[(eq + 1)..].Trim();
            if (value.Contains('@')) continue;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) continue;
            string key = part[..eq].Trim();
            into[key] = into.GetValueOrDefault(key) + v;
        }
    }

    public static void LogAndReset(string label)
    {
        try
        {
            Stats w;
            int windows;
            lock (_lock)
            {
                w = _window;
                _window = new Stats();
                _total.Add(w);
                windows = ++_windows;
            }

            Console.WriteLine($"ColdReadProbe window {label}: {Format(w)} worst={w.WorstTicks / TicksPerMicro:F0}us [{w.WorstReport}]");
            if (windows > 0)
            {
                lock (_lock)
                {
                    Console.WriteLine($"ColdReadProbe TOTAL windows={windows}: {Format(_total)}");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"ColdReadProbe log failed: {e}");
        }
    }

    private static string Format(Stats s)
    {
        StringBuilder sb = new();
        string[] names = ["acc", "slot"];
        for (int k = 0; k < 2; k++)
        {
            long c = Math.Max(1, s.Count[k]);
            sb.Append(CultureInfo.InvariantCulture, $"{names[k]} n={s.Count[k]} found={s.Found[k]} gets={s.Gets[k]} outerAvg={s.OuterTicks[k] / TicksPerMicro / c:F1}us nativeAvg={s.NativeTicks[k] / TicksPerMicro / c:F1}us outerSum={s.OuterTicks[k] / TicksPerMicro / 1000:F2}ms nativeSum={s.NativeTicks[k] / TicksPerMicro / 1000:F2}ms; ");
        }
        sb.Append(CultureInfo.InvariantCulture, $"otherNative n={s.OtherNativeGets} sum={s.OtherNativeTicks / TicksPerMicro / 1000:F2}ms; hist(us):");
        for (int i = 0; i < BucketsMicros.Length; i++)
        {
            string edge = BucketsMicros[i] == double.MaxValue ? "inf" : BucketsMicros[i].ToString(CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $" <{edge}:{s.BucketCount[i]}/{s.BucketTicks[i] / TicksPerMicro / 1000:F2}ms");
        }
        sb.Append(CultureInfo.InvariantCulture, $"; slow(>={SlowNativeMicros}us) n={s.SlowGets} perf=");
        AppendPerf(sb, s.SlowPerf, s.SlowGets);
        sb.Append(CultureInfo.InvariantCulture, $"; fast n={s.FastGets} perf=");
        AppendPerf(sb, s.FastPerf, s.FastGets);
        return sb.ToString();
    }

    private static void AppendPerf(StringBuilder sb, Dictionary<string, long> perf, long n)
    {
        List<KeyValuePair<string, long>> entries = [.. perf];
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        double d = Math.Max(1, n);
        foreach (KeyValuePair<string, long> kv in entries)
            sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}:{kv.Value / d:F1} ");
    }

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_set_perf_level(int level);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rocksdb_perfcontext_create();

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_perfcontext_reset(IntPtr context);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rocksdb_perfcontext_report(IntPtr context, byte excludeZeroCounters);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_free(IntPtr ptr);
}
