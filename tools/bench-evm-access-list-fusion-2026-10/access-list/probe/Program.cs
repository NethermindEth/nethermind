using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Probe;

interface ISetW<T> { bool Add(T c); void Clear(); int Snap(); void Restore(int s); }
readonly struct OldW<T>(JournalSetOld<T> s) : ISetW<T> { public bool Add(T c) => s.Add(c); public void Clear() => s.Clear(); public int Snap() => s.TakeSnapshot(); public void Restore(int x) => s.Restore(x); }
readonly struct NewW<T>(JournalSet<T> s) : ISetW<T> { public bool Add(T c) => s.Add(c); public void Clear() => s.Clear(); public int Snap() => s.TakeSnapshot(); public void Restore(int x) => s.Restore(x); }

static class P
{
    static Address RandAddr(Random r) { byte[] b = new byte[20]; r.NextBytes(b); return new Address(b); }
    static UInt256 RandSlot(Random r) { byte[] b = new byte[32]; r.NextBytes(b); return new UInt256(b, true); }

    // phase 1: cold adds of n items; phase 2: `hits` warm passes; phase 3: snapshot, add m more, restore; then clear.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Cycle<W, T>(W w, T[] items, int n, int hits, int m, long[] t) where W : struct, ISetW<T>
    {
        long sink = 0, a = Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) if (w.Add(items[i])) sink++;
        long b = Stopwatch.GetTimestamp();
        for (int h = 0; h < hits; h++) for (int i = 0; i < n; i++) if (w.Add(items[i])) sink++;
        long c = Stopwatch.GetTimestamp();
        int s = w.Snap();
        for (int i = n; i < n + m; i++) if (w.Add(items[i])) sink++;
        w.Restore(s);
        long d = Stopwatch.GetTimestamp();
        w.Clear();
        long e = Stopwatch.GetTimestamp();
        t[0] += b - a; t[1] += c - b; t[2] += d - c; t[3] += e - d;
        return sink;
    }

    static double[] Run<W, T>(W w, T[][] sets, int n, int hits, int m, int reps) where W : struct, ISetW<T>
    {
        long[] t = new long[4]; long sink = 0;
        for (int r = 0; r < reps; r++) sink += Cycle<W, T>(w, sets[r % sets.Length], n, hits, m, t);
        GC.KeepAlive(sink);
        double f = 1e9 / Stopwatch.Frequency / reps;
        return [t[0] * f / n, t[1] * f / Math.Max(1, n * hits), t[2] * f / Math.Max(1, m), t[3] * f];
    }

    static void Compare<T>(string name, T[][] sets, int n, int hits, int m, EqualityComparer<T> cmp, T[]? inflate)
    {
        var o = new JournalSetOld<T>(cmp); var nw = new JournalSet<T>(cmp);
        if (inflate != null) { foreach (var c in inflate) { o.Add(c); nw.Add(c); } o.Clear(); nw.Clear(); }
        int reps = Math.Max(200, 400000 / (n + m));
        for (int k = 0; k < 4; k++) { Run(new OldW<T>(o), sets, n, hits, m, reps); Run(new NewW<T>(nw), sets, n, hits, m, reps); }
        var ro = new List<double[]>(); var rn = new List<double[]>();
        for (int k = 0; k < 7; k++) { ro.Add(Run(new OldW<T>(o), sets, n, hits, m, reps)); rn.Add(Run(new NewW<T>(nw), sets, n, hits, m, reps)); }
        double Med(List<double[]> l, int i) { var v = l.Select(x => x[i]).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
        string[] lab = ["cold add", "warm hit", "add+restore", "clear(ns/cycle)"];
        Console.Write($"{name,-34}");
        for (int i = 0; i < 4; i++) Console.Write($" | {lab[i]} {Med(ro, i),6:F1}->{Med(rn, i),6:F1}");
        Console.WriteLine();
    }

    static void Main()
    {
        Random r = new(1);
        Address[] cs = Enumerable.Range(0, 8).Select(_ => RandAddr(r)).ToArray();
        StorageCell[][] Cells(int k, int len) => Enumerable.Range(0, k).Select(_ => Enumerable.Range(0, len).Select(_ => new StorageCell(cs[r.Next(8)], RandSlot(r))).ToArray()).ToArray();
        Address[][] Addrs(int k, int len) => Enumerable.Range(0, k).Select(_ => Enumerable.Range(0, len).Select(_ => RandAddr(r)).ToArray()).ToArray();
        StorageCell[] inflC = Cells(1, 60000)[0];
        Address[] inflA = Addrs(1, 20000)[0];
        for (int pass = 0; pass < 2; pass++)
        {
            Console.WriteLine($"--- pass {pass}");
            foreach (int n in new[] { 8, 50, 400, 4000 })
            {
                var sets = Cells(16, n + n / 4 + 1);
                Compare($"cells n={n}", sets, n, 2, n / 4 + 1, StorageCell.EqualityComparer, null);
                Compare($"cells n={n} inflated 60k", sets, n, 2, n / 4 + 1, StorageCell.EqualityComparer, inflC);
            }
            foreach (int n in new[] { 4, 20, 100 })
            {
                var sets = Addrs(16, n + n / 4 + 1);
                Compare($"addr n={n}", sets, n, 2, n / 4 + 1, Address.EqualityComparer, null);
                Compare($"addr n={n} inflated 20k", sets, n, 2, n / 4 + 1, Address.EqualityComparer, inflA);
            }
        }
    }
}
