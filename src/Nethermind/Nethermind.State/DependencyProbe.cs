// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State;

public sealed class DependencyProbe
{
    [ThreadStatic] private static DependencyProbe? _current;

    private const byte KindAccount = 0;
    private const byte KindBalance = 1;
    private const byte KindNonce = 2;
    private const byte KindCode = 3;
    private const byte KindSlot = 4;
    private const byte KindStorageAll = 5;
    private const int Cores = 8;

    private readonly struct Key(Address address, in UInt256 slot, byte kind) : IEquatable<Key>
    {
        public readonly Address Address = address;
        public readonly UInt256 Slot = slot;
        public readonly byte Kind = kind;
        public bool Equals(Key other) => Kind == other.Kind && Slot.Equals(other.Slot) && Address.Equals(other.Address);
        public override bool Equals(object? obj) => obj is Key k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Address.GetHashCode(), Slot.GetHashCode(), Kind);
    }

    private sealed class KeyState
    {
        public bool WrittenStrict;
        public bool WrittenNoCoinbase;
        public double CpStrict;
        public double CpNoCoinbase;
        public double PStrict;
        public double PNoCoinbase;
    }

    private readonly Dictionary<Key, KeyState> _keys = new(1 << 16);
    private readonly HashSet<Key> _reads = new(256);
    private readonly HashSet<Key> _writes = new(256);
    private readonly double[] _coresStrict = new double[Cores];
    private readonly double[] _coresNoCoinbase = new double[Cores];
    private Address _coinbase = Address.Zero;
    private long _txStart;
    private long _accesses;

    private int _txs;
    private int _indepStrict;
    private int _indepNoCoinbase;
    private double _totalMs;
    private double _indepStrictMs;
    private double _indepNoCoinbaseMs;
    private long _totalGas;
    private long _indepStrictGas;
    private long _indepNoCoinbaseGas;
    private double _cpStrict;
    private double _cpNoCoinbase;
    private double _maxTxMs;
    private readonly List<double> _txMs = new(16384);
    private readonly Dictionary<Address, double> _lastSlotWrite = new(4096);

    [ThreadStatic] private static DependencyProbe? _instance;
    private static DependencyProbe Instance => _instance ??= new DependencyProbe();

    public static void BeginBlock(Address coinbase)
    {
        DependencyProbe p = Instance;
        p._keys.Clear();
        p._coinbase = coinbase;
        Array.Clear(p._coresStrict);
        Array.Clear(p._coresNoCoinbase);
        p._txs = p._indepStrict = p._indepNoCoinbase = 0;
        p._totalMs = p._indepStrictMs = p._indepNoCoinbaseMs = p._cpStrict = p._cpNoCoinbase = p._maxTxMs = 0;
        p._totalGas = p._indepStrictGas = p._indepNoCoinbaseGas = 0;
        p._accesses = 0;
        p._txMs.Clear();
        p._lastSlotWrite.Clear();
    }

    public static void BeginTx()
    {
        DependencyProbe p = Instance;
        p._reads.Clear();
        p._writes.Clear();
        _current = p;
        p._txStart = Stopwatch.GetTimestamp();
    }

    public static void EndTx(ulong gasUsed)
    {
        DependencyProbe p = Instance;
        double ms = Stopwatch.GetElapsedTime(p._txStart).TotalMilliseconds;
        _current = null;
        p.Settle(ms, gasUsed);
    }

    public static void Abort() => _current = null;

    private void Settle(double ms, ulong gasU)
    {
        long gas = (long)gasU;
        bool conflictStrict = false, conflictNoCoinbase = false;
        double readyCpS = 0, readyCpN = 0, readyPS = 0, readyPN = 0;
        foreach (Key k in _reads)
        {
            if (!_keys.TryGetValue(k, out KeyState? s)) continue;
            conflictStrict |= s.WrittenStrict;
            readyCpS = Math.Max(readyCpS, s.CpStrict);
            readyPS = Math.Max(readyPS, s.PStrict);
            if (!k.Address.Equals(_coinbase))
            {
                conflictNoCoinbase |= s.WrittenNoCoinbase;
                readyCpN = Math.Max(readyCpN, s.CpNoCoinbase);
                readyPN = Math.Max(readyPN, s.PNoCoinbase);
            }
        }

        double finCpS = readyCpS + ms;
        double finCpN = readyCpN + ms;
        double finPS = Schedule(_coresStrict, readyPS, ms);
        double finPN = Schedule(_coresNoCoinbase, readyPN, ms);

        foreach (Key k in _writes)
        {
            ref KeyState? s = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_keys, k, out _);
            s ??= new KeyState();
            s.WrittenStrict = true;
            s.CpStrict = Math.Max(s.CpStrict, finCpS);
            s.PStrict = Math.Max(s.PStrict, finPS);
            if (!k.Address.Equals(_coinbase))
            {
                s.WrittenNoCoinbase = true;
                s.CpNoCoinbase = Math.Max(s.CpNoCoinbase, finCpN);
                s.PNoCoinbase = Math.Max(s.PNoCoinbase, finPN);
            }
        }

        _txs++;
        _totalMs += ms;
        foreach (Key k in _writes) if (k.Kind == KindSlot) _lastSlotWrite[k.Address] = _totalMs;
        _totalGas += gas;
        _maxTxMs = Math.Max(_maxTxMs, ms);
        _txMs.Add(ms);
        _cpStrict = Math.Max(_cpStrict, finCpS);
        _cpNoCoinbase = Math.Max(_cpNoCoinbase, finCpN);
        if (!conflictStrict) { _indepStrict++; _indepStrictMs += ms; _indepStrictGas += gas; }
        if (!conflictNoCoinbase) { _indepNoCoinbase++; _indepNoCoinbaseMs += ms; _indepNoCoinbaseGas += gas; }
    }

    private static double Schedule(double[] cores, double ready, double ms)
    {
        int min = 0;
        for (int i = 1; i < cores.Length; i++) if (cores[i] < cores[min]) min = i;
        double finish = Math.Max(ready, cores[min]) + ms;
        cores[min] = finish;
        return finish;
    }

    public static void EndBlock(ulong blockNumber)
    {
        DependencyProbe p = Instance;
        _current = null;
        double pS = 0, pN = 0;
        for (int i = 0; i < Cores; i++) { pS = Math.Max(pS, p._coresStrict[i]); pN = Math.Max(pN, p._coresNoCoinbase[i]); }
        p._txMs.Sort();
        double top1 = 0;
        int topN = Math.Max(1, p._txMs.Count / 100);
        for (int i = p._txMs.Count - topN; i < p._txMs.Count; i++) if (i >= 0) top1 += p._txMs[i];
        int ws = 0, ws50 = 0, ws75 = 0, ws90 = 0, wc = 0, wc50 = 0, wc75 = 0, wc90 = 0;
        double total = Math.Max(p._totalMs, 1e-9);
        foreach (KeyValuePair<Key, KeyState> e in p._keys)
        {
            if (e.Key.Kind != KindSlot || !e.Value.WrittenStrict) continue;
            double f = p._lastSlotWrite.TryGetValue(e.Key.Address, out double t) ? t / total : 1;
            ws++;
            if (f <= 0.5) ws50++;
            if (f <= 0.75) ws75++;
            if (f <= 0.9) ws90++;
        }
        foreach (double t in p._lastSlotWrite.Values)
        {
            double f = t / total;
            wc++;
            if (f <= 0.5) wc50++;
            if (f <= 0.75) wc75++;
            if (f <= 0.9) wc90++;
        }
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"DEPPROBE block={blockNumber} txs={p._txs} gas={p._totalGas} tx_ms={p._totalMs:F3} max_tx_ms={p._maxTxMs:F3} top1pct_ms={top1:F3} accesses={p._accesses} keys={p._keys.Count}");
        sb.Append(CultureInfo.InvariantCulture, $" indep_s={p._indepStrict} indep_s_ms={p._indepStrictMs:F3} indep_s_gas={p._indepStrictGas} cp_s_ms={p._cpStrict:F3} p8_s_ms={pS:F3}");
        sb.Append(CultureInfo.InvariantCulture, $" indep_n={p._indepNoCoinbase} indep_n_ms={p._indepNoCoinbaseMs:F3} indep_n_gas={p._indepNoCoinbaseGas} cp_n_ms={p._cpNoCoinbase:F3} p8_n_ms={pN:F3}");
        sb.Append(CultureInfo.InvariantCulture, $" wslots={ws} wslots50={ws50} wslots75={ws75} wslots90={ws90} wcontracts={wc} wc50={wc50} wc75={wc75} wc90={wc90}");
        Console.WriteLine(sb.ToString());
    }

    internal static void ReadAccount(Address a) => _current?.Read(new Key(a, default, KindAccount));
    internal static void ReadBalance(Address a) => _current?.Read(new Key(a, default, KindBalance));
    internal static void ReadNonce(Address a) => _current?.Read(new Key(a, default, KindNonce));
    internal static void ReadCode(Address a) => _current?.Read(new Key(a, default, KindCode));

    internal static void ReadSlot(in StorageCell cell)
    {
        DependencyProbe? p = _current;
        if (p is null) return;
        p.Read(new Key(cell.Address, cell.Index, KindSlot));
        p.Read(new Key(cell.Address, default, KindStorageAll));
    }

    internal static void WriteBalance(Address a) => _current?.Write(a, KindBalance);
    internal static void WriteNonce(Address a) => _current?.Write(a, KindNonce);
    internal static void WriteCode(Address a) => _current?.Write(a, KindCode);

    internal static void WriteAll(Address a)
    {
        DependencyProbe? p = _current;
        if (p is null) return;
        p.Write(a, KindBalance);
        p.Write(a, KindNonce);
        p.Write(a, KindCode);
        p.Write(a, KindStorageAll);
    }

    internal static void WriteStorageAll(Address a) => _current?.Write(a, KindStorageAll);

    internal static void WriteSlot(in StorageCell cell)
    {
        DependencyProbe? p = _current;
        if (p is null) return;
        p._accesses++;
        p._writes.Add(new Key(cell.Address, cell.Index, KindSlot));
    }

    private void Read(Key k)
    {
        _accesses++;
        _reads.Add(k);
    }

    private void Write(Address a, byte kind)
    {
        _accesses++;
        _writes.Add(new Key(a, default, kind));
        _writes.Add(new Key(a, default, KindAccount));
    }
}
