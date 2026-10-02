// Verifier probe: differential and structural checks of the new JournalSet against the old one and a model,
// plus probe-length statistics under attacker-chosen inputs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Int256;
using NewSet = Probe.New.JournalSet<int>;

internal static class Program
{
    private const uint Phi = 0x9E3779B9;
    private static readonly uint InvPhi = Inverse(Phi);
    private static int _failures;

    private static uint Inverse(uint a)
    {
        uint x = a; // Newton iteration for the inverse modulo 2^32
        for (int i = 0; i < 5; i++) x *= 2 - a * x;
        return x;
    }

    private sealed class FuncComparer<T>(Func<T, int> hash, string name) : EqualityComparer<T>
    {
        public override bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x, y);
        public override int GetHashCode(T obj) => hash(obj);
        public override string ToString() => name;
    }

    private static void Fail(string message)
    {
        _failures++;
        if (_failures < 40) Console.WriteLine("FAIL " + message);
    }

    // ---------- structural check of the new set via reflection ----------
    private static readonly Dictionary<Type, (FieldInfo items, FieldInfo hashes, FieldInfo table, FieldInfo size)> Fields = new();

    private static (int occupied, int maxDisp, double meanDisp, int maxCluster) CheckStructure<T>(object set, string ctx)
    {
        Type t = set.GetType();
        if (!Fields.TryGetValue(t, out var f))
        {
            BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
            f = (t.GetField("_items", bf)!, t.GetField("_hashes", bf)!, t.GetField("_table", bf)!, t.GetField("_tableSize", bf)!);
            Fields[t] = f;
        }

        List<T> items = (List<T>)f.items.GetValue(set)!;
        int[] hashes = (int[])f.hashes.GetValue(set)!;
        Array table = (Array)f.table.GetValue(set)!;
        int size = (int)f.size.GetValue(set)!;
        Type slotType = table.GetType().GetElementType()!;
        if (slotType.GetFields().Length != 2 || slotType.GetFields()[0].Name != "Hash" || slotType.GetFields()[1].Name != "Entry") Fail("unexpected slot layout");
        int n = items.Count;
        int[] slotHash = new int[table.Length], slotEntry = new int[table.Length];
        ReadOnlySpan<int> raw = MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.As<byte, int>(ref MemoryMarshal.GetArrayDataReference(table)), table.Length * 2);
        for (int i = 0; i < table.Length; i++)
        {
            slotHash[i] = raw[2 * i];
            slotEntry[i] = raw[2 * i + 1];
        }

        if ((size & (size - 1)) != 0 || size < 16 || size > table.Length) Fail($"{ctx}: bad table size {size}/{table.Length}");
        if (n > size / 2) Fail($"{ctx}: load {n}/{size} above half");
        int occupied = 0;
        bool[] seen = new bool[n + 1];
        for (int i = 0; i < table.Length; i++)
        {
            int e = slotEntry[i];
            if (e == 0) continue;
            if (i >= size) { Fail($"{ctx}: entry {e} at {i} past table size {size}"); continue; }
            occupied++;
            if (e < 0 || e > n) { Fail($"{ctx}: stale entry {e} (count {n}) at slot {i}"); continue; }
            if (seen[e]) Fail($"{ctx}: entry {e} twice");
            seen[e] = true;
            if (slotHash[i] != hashes[e - 1]) Fail($"{ctx}: slot hash differs from stored hash for entry {e}");
        }

        if (occupied != n) Fail($"{ctx}: occupied {occupied} != count {n}");

        // Canonical form: the table must equal the one built by inserting the stored hashes in order.
        int shift = 32 - BitOperations.Log2((uint)size);
        int[] canon = new int[size];
        int maxDisp = 0;
        long sumDisp = 0;
        for (int i = 0; i < n; i++)
        {
            uint home = ((uint)hashes[i] * Phi) >> shift;
            uint idx = home;
            while (canon[idx] != 0) idx = (idx + 1) & (uint)(size - 1);
            canon[idx] = i + 1;
            int disp = (int)((idx - home) & (uint)(size - 1));
            maxDisp = Math.Max(maxDisp, disp);
            sumDisp += disp;
        }

        for (int i = 0; i < size; i++)
        {
            if (canon[i] != slotEntry[i])
            {
                Fail($"{ctx}: not canonical at slot {i}: table {slotEntry[i]} vs in-order insertion {canon[i]}");
                break;
            }
        }

        int maxCluster = 0, run = 0;
        for (int i = 0; i < 2 * size; i++)
        {
            if (canon[i % size] != 0) { run++; maxCluster = Math.Max(maxCluster, run); }
            else run = 0;
            if (run >= size) break;
        }

        return (occupied, maxDisp, n == 0 ? 0 : (double)sumDisp / n, maxCluster);
    }

    // ---------- differential run ----------
    private static void Differential<T>(string name, Func<Random, T> pick, Func<int, T> fresh, EqualityComparer<T> comparer, int steps, int seed, bool checkStructure = true)
    {
        Random r = new(seed);
        Probe.New.JournalSet<T> nw = new(comparer);
        Probe.Old.JournalSet<T> old = new(comparer);
        List<T> model = [];
        HashSet<T> modelSet = new(comparer);
        Stack<int> snaps = new();
        int freshCounter = 0, grows = 0, restores = 0, clears = 0, deepRestores = 0;
        long maxDisp = 0, maxCluster = 0;
        List<T> universe = [];

        for (int step = 0; step < steps; step++)
        {
            int roll = r.Next(1000);
            if (roll < 600)
            {
                T item = r.Next(4) == 0 && universe.Count > 0 ? universe[r.Next(universe.Count)] : pick(r);
                universe.Add(item);
                bool expected = modelSet.Add(item);
                if (expected) model.Add(item);
                bool a = nw.Add(item), b = old.Add(item);
                if (a != expected || b != expected) Fail($"{name} step {step}: Add {item} new={a} old={b} model={expected}");
            }
            else if (roll < 700)
            {
                T item = universe.Count > 0 && r.Next(2) == 0 ? universe[r.Next(universe.Count)] : pick(r);
                bool expected = modelSet.Contains(item);
                if (nw.Contains(item) != expected || old.Contains(item) != expected) Fail($"{name} step {step}: Contains {item}");
            }
            else if (roll < 800)
            {
                int s = nw.TakeSnapshot();
                if (s != old.TakeSnapshot()) Fail($"{name} step {step}: snapshot differs");
                snaps.Push(s);
            }
            else if (roll < 940)
            {
                if (snaps.Count == 0) continue;
                int pops = r.Next(3) == 0 ? r.Next(1, snaps.Count + 1) : 1;
                int s = 0;
                for (int i = 0; i < pops; i++) s = snaps.Pop();
                if (pops > 1) deepRestores++;
                nw.Restore(s);
                old.Restore(s);
                for (int i = model.Count - 1; i > s; i--) modelSet.Remove(model[i]);
                model.RemoveRange(s + 1, model.Count - s - 1);
                restores++;
            }
            else if (roll < 975)
            {
                // Burst: a frame warms many new items, so the table grows between the snapshot and its restore.
                int burst = r.Next(16, r.Next(10) == 0 ? 6000 : 400);
                if (r.Next(2) == 0) snaps.Push(nw.TakeSnapshot() + 0 * old.TakeSnapshot());
                for (int i = 0; i < burst; i++)
                {
                    T item = fresh(freshCounter++);
                    bool expected = modelSet.Add(item);
                    if (expected) model.Add(item);
                    bool a = nw.Add(item), b = old.Add(item);
                    if (a != expected || b != expected) Fail($"{name} step {step}: burst Add new={a} old={b} model={expected}");
                }

                grows++;
            }
            else
            {
                nw.Clear();
                old.Clear();
                model.Clear();
                modelSet.Clear();
                snaps.Clear();
                clears++;
                if (universe.Count > 20000) universe.RemoveRange(0, universe.Count - 2000);
            }

            if (nw.Count != model.Count || old.Count != model.Count) Fail($"{name} step {step}: Count new={nw.Count} old={old.Count} model={model.Count}");
            if (step % 211 == 0 || roll >= 940)
            {
                if (!nw.SequenceEqual(model, comparer)) Fail($"{name} step {step}: order differs");
                foreach (T u in universe.Count > 3000 ? universe.Skip(universe.Count - 3000) : universe)
                {
                    bool expected = modelSet.Contains(u);
                    if (nw.Contains(u) != expected) { Fail($"{name} step {step}: Contains({u}) new={!expected}"); break; }
                }

                if (checkStructure)
                {
                    var st = CheckStructure<T>(nw, $"{name} step {step}");
                    maxDisp = Math.Max(maxDisp, st.maxDisp);
                    maxCluster = Math.Max(maxCluster, st.maxCluster);
                }
            }
        }

        Console.WriteLine($"diff {name,-28} steps={steps} restores={restores} deep={deepRestores} bursts={grows} clears={clears} maxDisp={maxDisp} maxCluster={maxCluster} failures={_failures}");
    }

    // ---------- probe lengths of the new table vs the old HashSet ----------
    private static (int maxChain, double meanChain) HashSetChains<T>(HashSet<T> set)
    {
        BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
        int[] buckets = (int[])typeof(HashSet<T>).GetField("_buckets", bf)!.GetValue(set)!;
        Array entries = (Array)typeof(HashSet<T>).GetField("_entries", bf)!.GetValue(set)!;
        FieldInfo next = entries.GetType().GetElementType()!.GetField("Next")!;
        int max = 0;
        long sum = 0, items = 0;
        foreach (int b in buckets)
        {
            int len = 0;
            for (int i = b - 1; i >= 0; i = (int)next.GetValue(entries.GetValue(i))!) { len++; sum += len; items++; }
            max = Math.Max(max, len);
        }

        return (max, items == 0 ? 0 : (double)sum / items);
    }

    private static void ProbeLengths<T>(string name, IEnumerable<T> keys, EqualityComparer<T> comparer)
    {
        Probe.New.JournalSet<T> nw = new(comparer);
        HashSet<T> hs = new(comparer);
        foreach (T k in keys) { nw.Add(k); hs.Add(k); }
        var st = CheckStructure<T>(nw, name);
        var hc = HashSetChains(hs);
        Console.WriteLine($"probe {name,-40} n={nw.Count,6} new: meanDisp={st.meanDisp,6:F2} maxDisp={st.maxDisp,5} maxCluster={st.maxCluster,5} | HashSet: meanChain={hc.meanChain,5:F2} maxChain={hc.maxChain,3}");
    }


    // ---------- timing: lookups after an attacker filled the set ----------
    private static double TimeLookups<TSet, T>(TSet set, T[] lookups, Func<TSet, T, bool> contains, int rounds)
    {
        double best = double.MaxValue;
        for (int round = 0; round < rounds; round++)
        {
            long start = Stopwatch.GetTimestamp();
            int hits = 0;
            for (int i = 0; i < lookups.Length; i++) if (contains(set, lookups[i])) hits++;
            double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / lookups.Length;
            if (hits < 0) Console.WriteLine();
            best = Math.Min(best, ns);
        }

        return best;
    }

    private static void BenchCase<T>(string name, T[] keys, T[] misses, EqualityComparer<T> comparer, int lookups = 2_000_000)
    {
        Random r = new(5);
        Probe.New.JournalSet<T> nw = new(comparer);
        Probe.Old.JournalSet<T> old = new(comparer);
        foreach (T k in keys) { nw.Add(k); old.Add(k); }
        T[] hitOrder = Enumerable.Range(0, lookups).Select(_ => keys[r.Next(keys.Length)]).ToArray();
        T[] missOrder = Enumerable.Range(0, lookups).Select(_ => misses[r.Next(misses.Length)]).ToArray();
        var results = new List<string>();
        for (int pass = 0; pass < 2; pass++)
        {
            // ABAB: new, old, new, old
            double nh = TimeLookups(nw, hitOrder, static (s, x) => s.Contains(x), 3);
            double oh = TimeLookups(old, hitOrder, static (s, x) => s.Contains(x), 3);
            double nm = TimeLookups(nw, missOrder, static (s, x) => s.Contains(x), 3);
            double om = TimeLookups(old, missOrder, static (s, x) => s.Contains(x), 3);
            results.Add($"pass{pass}: hit new {nh:F1} old {oh:F1} | miss new {nm:F1} old {om:F1}");
        }

        Console.WriteLine($"bench {name,-34} n={keys.Length,7} ns/lookup {string.Join(" ; ", results)}");
    }

    private static void Bench(int scale)
    {
        Address one = Addr(UInt256.Parse("0x00000000219ab540356cbb839cbe05303d7705fa"));
        foreach (int n in new[] { 1000, 30_000, 120_000 })
        {
            int m = n / scale;
            StorageCell[] keys = Enumerable.Range(0, m).Select(i => new StorageCell(one, (UInt256)(ulong)i)).ToArray();
            StorageCell[] misses = Enumerable.Range(0, m).Select(i => new StorageCell(one, (UInt256)(ulong)(i + 10_000_000))).ToArray();
            BenchCase("cell sequential slots (real hash)", keys, misses, StorageCell.EqualityComparer);
        }

        // Known-hash worst cases, which an attacker can only build if it knows the seed: homes packed into one
        // window of the new table, and every key on one HashSet bucket (equal hashes).
        foreach (int n in new[] { 2000, 8000 })
        {
            int m = n / scale;
            int[] keys = Enumerable.Range(0, m).ToArray();
            int[] misses = Enumerable.Range(1_000_000, m).ToArray();
            BenchCase("int window-packed (known hash)", keys, misses, new FuncComparer<int>(x => (int)((uint)(x % 8192) * InvPhi), "win"), 200_000);
            BenchCase("int all-equal hash (known hash)", keys, misses, new FuncComparer<int>(static _ => 0, "const"), 200_000);
            BenchCase("int identity", keys, misses, new FuncComparer<int>(static x => x, "id"), 200_000);
        }
    }

    private static Address Addr(UInt256 v)
    {
        byte[] be = v.ToBigEndian();
        return new Address(be.AsSpan(12, 20).ToArray());
    }

    private static int Main(string[] args)
    {
        // No-op on the host build; the zkEVM build needs its guest seed before anything hashes.
        Nethermind.Core.Extensions.SpanExtensions.SeedHashes(new UInt256(0x243F6A8885A308D3UL, 0x13198A2E03707344UL, 0xA4093822299F31D0UL, 0x082EFA98EC4E6C89UL));
        string mode = args.Length > 0 ? args[0] : "all";
        int steps = args.Length > 1 ? int.Parse(args[1]) : 60000;
        Console.WriteLine($"InvPhi=0x{InvPhi:x8} check={(uint)(InvPhi * Phi)}");

        if (mode is "all" or "diff")
        {
            // Weak and adversarial int hashes. InvPhiTop puts every home on the last slot of any table size, so
            // every probe run wraps around the end; InvPhiWin packs homes into a narrow window to build long clusters.
            var comparers = new (string, Func<int, int>)[]
            {
                ("const0", static _ => 0),
                ("mod3", static x => x % 3),
                ("mod64", static x => x % 64),
                ("identity", static x => x),
                ("invPhiTop", x => (int)((uint)(-(x % 4096) - 1) * InvPhi)),
                ("invPhiWin", x => (int)((uint)(x % 8192) * InvPhi)),
                ("invPhiSpread", x => (int)((uint)(x << 19) * InvPhi)),
                ("hiBitsOnly", static x => x << 20),
            };
            int seed = 1;
            foreach ((string n, Func<int, int> h) in comparers)
            {
                int s = n is "const0" or "invPhiTop" ? steps / 6 : steps;
                Differential($"int/{n}", r => r.Next(5000), i => 100_000 + i, new FuncComparer<int>(h, n), s, seed++, checkStructure: true);
            }

            Address[] addrPool = Enumerable.Range(0, 300).Select(i => Addr((UInt256)(ulong)(i * 7919 + 1))).ToArray();
            Differential("StorageCell/real", r => new StorageCell(addrPool[r.Next(8)], (UInt256)(ulong)r.Next(3000)),
                i => new StorageCell(addrPool[i % 300], (UInt256)(ulong)(1_000_000 + i)), StorageCell.EqualityComparer, steps, 77);
            Differential<Address?>("Address/real+null", r => r.Next(50) == 0 ? null : addrPool[r.Next(300)],
                i => Addr((UInt256)(ulong)(5_000_000 + i)), Address.EqualityComparer!, steps, 78);
        }

        if (mode is "all" or "probe")
        {
            Address one = Addr(UInt256.Parse("0x00000000219ab540356cbb839cbe05303d7705fa"));
            foreach (int n in new[] { 100, 1000, 10_000, 30_000, 60_000 })
            {
                ProbeLengths($"cell sequential slots", Enumerable.Range(0, n).Select(i => new StorageCell(one, (UInt256)(ulong)i)), StorageCell.EqualityComparer);
                ProbeLengths($"cell slots i<<128", Enumerable.Range(0, n).Select(i => new StorageCell(one, (UInt256)(ulong)i << 128)), StorageCell.EqualityComparer);
                ProbeLengths($"cell slots i<<224", Enumerable.Range(0, n).Select(i => new StorageCell(one, (UInt256)(ulong)i << 224)), StorageCell.EqualityComparer);
                ProbeLengths($"cell same slot, seq addresses", Enumerable.Range(0, n).Select(i => new StorageCell(Addr((UInt256)(ulong)i), UInt256.One)), StorageCell.EqualityComparer);
                ProbeLengths($"address sequential", Enumerable.Range(0, n).Select(i => Addr((UInt256)(ulong)i)), Address.EqualityComparer);
                ProbeLengths($"address i<<140", Enumerable.Range(0, n).Select(i => Addr((UInt256)(ulong)i << 140)), Address.EqualityComparer);
            }
        }

        if (mode is "bench") Bench(steps > 0 && steps < 100 ? steps : 1);

        Console.WriteLine($"TOTAL_FAILURES {_failures}");
        return _failures == 0 ? 0 : 1;
    }
}
