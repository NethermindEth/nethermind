// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;

public static class IdleProbe
{
    public static readonly bool Recipes = Environment.GetEnvironmentVariable("IDLEPROBE_RECIPES") == "1";
    [ThreadStatic] public static bool Active;
    [ThreadStatic] public static long ColdCount;
    [ThreadStatic] public static long ColdTicks;

    private const int MaxDepth = 3;
    private const int MaxDelta = 16;
    private const int MaxRecipesPerContext = 512;

    private const byte KConst = 0;
    private const byte KData = 1;
    private const byte KSender = 2;
    private const byte KTo = 3;
    private const byte K32 = 4;
    private const byte K64 = 5;

    private sealed class Node(byte kind, int arg, in UInt256 value, Node? a, Node? b, string key)
    {
        public readonly byte Kind = kind;
        public readonly int Arg = arg;
        public readonly UInt256 Value = value;
        public readonly Node? A = a;
        public readonly Node? B = b;
        public readonly string Key = key;
    }

    private sealed class Recipe(Node address, Node slot)
    {
        public readonly Node Address = address;
        public readonly Node Slot = slot;
        public readonly string Key = address.Key + "|" + slot.Key;
    }

    private readonly record struct Ctx(Address To, uint Selector);
    private readonly record struct Cell(UInt256 Address, UInt256 Slot);

    private sealed class Table
    {
        public readonly Dictionary<Ctx, Dictionary<string, Recipe>> Map = [];
        public long Overflow;

        public bool Add(in Ctx ctx, Recipe r)
        {
            if (!Map.TryGetValue(ctx, out Dictionary<string, Recipe>? set)) Map[ctx] = set = [];
            if (set.ContainsKey(r.Key)) return false;
            if (set.Count >= MaxRecipesPerContext) { Overflow++; return false; }
            set[r.Key] = r;
            return true;
        }
    }

    [ThreadStatic] private static Table? _strict;
    private static Table Strict => _strict ??= new();
    [ThreadStatic] private static Table? _lenient;
    private static Table Lenient => _lenient ??= new();
    [ThreadStatic] private static List<(Ctx, Recipe)>? _pending;
    private static List<(Ctx, Recipe)> Pending => _pending ??= [];

    [ThreadStatic] private static Dictionary<UInt256, (UInt256 W0, UInt256 W1, bool Two)>? _keccaks;
    private static Dictionary<UInt256, (UInt256 W0, UInt256 W1, bool Two)> Keccaks => _keccaks ??= [];
    [ThreadStatic] private static Dictionary<UInt256, int>? _dataWords;
    private static Dictionary<UInt256, int> DataWords => _dataWords ??= [];
    [ThreadStatic] private static HashSet<Cell>? _predStrict;
    private static HashSet<Cell> PredStrict => _predStrict ??= [];
    [ThreadStatic] private static HashSet<Cell>? _predLenient;
    private static HashSet<Cell> PredLenient => _predLenient ??= [];
    [ThreadStatic] private static HashSet<Cell>? _blockSeen;
    private static HashSet<Cell> BlockSeen => _blockSeen ??= [];
    [ThreadStatic] private static HashSet<Cell>? _txDerived;
    private static HashSet<Cell> TxDerived => _txDerived ??= [];

    [ThreadStatic] private static ReadOnlyMemory<byte> _data;
    [ThreadStatic] private static UInt256 _sender;
    [ThreadStatic] private static UInt256 _to;
    [ThreadStatic] private static Ctx _ctx;
    [ThreadStatic] private static bool _hasCtx;

    [ThreadStatic] private static long _sloads, _uniq, _uniqS, _uniqL, _cold, _coldS, _coldL, _coldAll, _predS, _predL, _predSHit;
    [ThreadStatic] private static long _coldTicks, _coldTicksS, _coldTicksL, _coldAllTicks, _uniqConstSlot;

    [ThreadStatic] private static long _blockStart, _execStart, _execWallTicks;
    [ThreadStatic] private static (long Cpu, long Rq, long Slices) _schedStart, _execSchedStart, _execSched;
    [ThreadStatic] private static (long V, long Iv) _csStart;
    [ThreadStatic] private static TimeSpan _gcStart;
    [ThreadStatic] private static long _execColdStart;

    private static readonly UInt256 Zero = UInt256.Zero;

    public static void BeginBlock()
    {
        Active = false;
        _blockStart = Stopwatch.GetTimestamp();
        _schedStart = ReadSched();
        _csStart = ReadCs();
        _gcStart = GC.GetTotalPauseDuration();
        BlockSeen.Clear();
        _sloads = _uniq = _uniqS = _uniqL = _cold = _coldS = _coldL = _coldAll = _predS = _predL = _predSHit = 0;
        _coldTicks = _coldTicksS = _coldTicksL = _coldAllTicks = _uniqConstSlot = 0;
        _execWallTicks = 0;
        _execSched = default;
        Pending.Clear();
    }

    public static void BeginExec()
    {
        _execStart = Stopwatch.GetTimestamp();
        _execSchedStart = ReadSched();
        _execColdStart = ColdCount;
        ColdTicks = 0;
    }

    public static void EndExec()
    {
        Active = false;
        _execWallTicks = Stopwatch.GetTimestamp() - _execStart;
        (long c, long r, long s) = ReadSched();
        _execSched = (c - _execSchedStart.Cpu, r - _execSchedStart.Rq, s - _execSchedStart.Slices);
        _coldAll = ColdCount - _execColdStart;
        _coldAllTicks = ColdTicks;
    }

    public static void BeginTx(ReadOnlyMemory<byte> data, Address? sender, Address? to)
    {
        Keccaks.Clear();
        DataWords.Clear();
        PredStrict.Clear();
        PredLenient.Clear();
        TxDerived.Clear();
        _data = data;
        _sender = Word(sender);
        _to = Word(to);
        _hasCtx = to is not null;
        ReadOnlySpan<byte> d = data.Span;
        if (_hasCtx)
        {
            uint sel = d.Length >= 4 ? (uint)(d[0] << 24 | d[1] << 16 | d[2] << 8 | d[3]) : 0u;
            _ctx = new Ctx(to!, sel);
            for (int k = 0; 4 + 32 * (k + 1) <= d.Length && k < 512; k++)
            {
                UInt256 w = new(d.Slice(4 + 32 * k, 32), true);
                DataWords.TryAdd(w, k);
            }
            Predict(Strict, PredStrict);
            Predict(Lenient, PredLenient);
            _predS += PredStrict.Count;
            _predL += PredLenient.Count;
        }
        Active = true;
    }

    public static void EndTx() => Active = false;

    public static void EndBlock(ulong number, int txs, long gas)
    {
        Active = false;
        foreach ((Ctx c, Recipe r) in Pending) Strict.Add(in c, r);
        Pending.Clear();
        long wall = Stopwatch.GetTimestamp() - _blockStart;
        (long cpu, long rq, long slices) = ReadSched();
        (long v, long iv) = ReadCs();
        double gcMs = (GC.GetTotalPauseDuration() - _gcStart).TotalMilliseconds;
        double f = 1000.0 / Stopwatch.Frequency;
        double wallMs = wall * f;
        double cpuMs = (cpu - _schedStart.Cpu) / 1e6;
        double rqMs = (rq - _schedStart.Rq) / 1e6;
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"IDLEPROBE block={number} txs={txs} gas={gas} wall_ms={wallMs:F3} cpu_ms={cpuMs:F3} rq_ms={rqMs:F3} gc_ms={gcMs:F3} vcs={v - _csStart.V} ivcs={iv - _csStart.Iv} slices={slices - _schedStart.Slices}");
        sb.Append(CultureInfo.InvariantCulture, $" exec_wall_ms={_execWallTicks * f:F3} exec_cpu_ms={_execSched.Cpu / 1e6:F3} exec_rq_ms={_execSched.Rq / 1e6:F3} cold_all={_coldAll} cold_all_ms={_coldAllTicks * f:F3}");
        sb.Append(CultureInfo.InvariantCulture, $" sloads={_sloads} uniq={_uniq} uniq_s={_uniqS} uniq_l={_uniqL} uniq_const={_uniqConstSlot} cold={_cold} cold_s={_coldS} cold_l={_coldL} cold_ms={_coldTicks * f:F3} cold_s_ms={_coldTicksS * f:F3} cold_l_ms={_coldTicksL * f:F3}");
        sb.Append(CultureInfo.InvariantCulture, $" pred_s={_predS} pred_s_hit={_predSHit} pred_l={_predL} ctx={Strict.Map.Count} ovf={Strict.Overflow}");
        Console.WriteLine(sb.ToString());
    }

    public static void OnKeccak(ReadOnlySpan<byte> input, in ValueHash256 output)
    {
        if (input.Length != 32 && input.Length != 64) return;
        UInt256 o = new(output.Bytes, true);
        UInt256 w0 = new(input[..32], true);
        UInt256 w1 = input.Length == 64 ? new UInt256(input.Slice(32, 32), true) : Zero;
        Keccaks.TryAdd(o, (w0, w1, input.Length == 64));
    }

    public static void OnStorageRead(Address address, in UInt256 slot, bool cold, long coldTicks)
    {
        _sloads++;
        UInt256 aw = Word(address);
        Cell cell = new(aw, slot);
        bool inS = PredStrict.Contains(cell);
        bool inL = PredLenient.Contains(cell);
        if (BlockSeen.Add(cell))
        {
            _uniq++;
            if (inS) { _uniqS++; _predSHit++; }
            if (inL) _uniqL++;
        }
        if (cold)
        {
            _cold++;
            _coldTicks += coldTicks;
            if (inS) { _coldS++; _coldTicksS += coldTicks; }
            if (inL) { _coldL++; _coldTicksL += coldTicks; }
        }
        if (!_hasCtx || inL || !TxDerived.Add(cell)) return;
        Node addrNode = DeriveAddress(in aw);
        Node slotNode = Derive(in slot, 0);
        if (slotNode.Kind == KConst) _uniqConstSlot++;
        Recipe r = new(addrNode, slotNode);
        Lenient.Add(in _ctx, r);
        Pending.Add((_ctx, r));
    }

    private static void Predict(Table t, HashSet<Cell> into)
    {
        if (!t.Map.TryGetValue(_ctx, out Dictionary<string, Recipe>? set)) return;
        foreach (Recipe r in set.Values)
        {
            if (Eval(r.Address, out UInt256 a) && Eval(r.Slot, out UInt256 s)) into.Add(new Cell(a, s));
        }
    }

    private static bool Eval(Node n, out UInt256 v)
    {
        switch (n.Kind)
        {
            case KConst: v = n.Value; return true;
            case KSender: v = _sender; return true;
            case KTo: v = _to; return true;
            case KData:
                {
                    ReadOnlySpan<byte> d = _data.Span;
                    int off = 4 + 32 * n.Arg;
                    if (off + 32 > d.Length) { v = default; return false; }
                    v = new UInt256(d.Slice(off, 32), true);
                    return true;
                }
            case K32:
                {
                    if (!Eval(n.A!, out UInt256 x)) { v = default; return false; }
                    Span<byte> buf = stackalloc byte[32];
                    x.ToBigEndian(buf);
                    UInt256 h = new(ValueKeccak.Compute(buf).Bytes, true);
                    UInt256.Add(in h, in n.Value, out v);
                    return true;
                }
            default:
                {
                    if (!Eval(n.A!, out UInt256 x) || !Eval(n.B!, out UInt256 y)) { v = default; return false; }
                    Span<byte> buf = stackalloc byte[64];
                    x.ToBigEndian(buf[..32]);
                    y.ToBigEndian(buf.Slice(32, 32));
                    UInt256 h = new(ValueKeccak.Compute(buf).Bytes, true);
                    UInt256.Add(in h, in n.Value, out v);
                    return true;
                }
        }
    }

    private static Node DeriveAddress(in UInt256 a)
    {
        if (a == _to) return new Node(KTo, 0, Zero, null, null, "T");
        if (DataWords.TryGetValue(a, out int k)) return new Node(KData, k, Zero, null, null, "D" + k.ToString(CultureInfo.InvariantCulture));
        return Const(in a);
    }

    private static Node Derive(in UInt256 w, int depth)
    {
        if (depth < MaxDepth)
        {
            for (int delta = 0; delta < MaxDelta; delta++)
            {
                UInt256 dv0 = (ulong)delta;
                if (w < dv0) break;
                UInt256.Subtract(in w, in dv0, out UInt256 baseHash);
                if (!Keccaks.TryGetValue(baseHash, out (UInt256 W0, UInt256 W1, bool Two) pre)) continue;
                UInt256 dv = (ulong)delta;
                Node a = Derive(in pre.W0, depth + 1);
                if (!pre.Two) return new Node(K32, 0, in dv, a, null, "K(" + a.Key + ")+" + delta.ToString(CultureInfo.InvariantCulture));
                Node b = Derive(in pre.W1, depth + 1);
                return new Node(K64, 0, in dv, a, b, "K(" + a.Key + "," + b.Key + ")+" + delta.ToString(CultureInfo.InvariantCulture));
            }
        }
        if (depth > 0)
        {
            if (w == _sender && !w.IsZero) return new Node(KSender, 0, Zero, null, null, "S");
            if (w == _to && !w.IsZero) return new Node(KTo, 0, Zero, null, null, "T");
            if (DataWords.TryGetValue(w, out int k)) return new Node(KData, k, Zero, null, null, "D" + k.ToString(CultureInfo.InvariantCulture));
        }
        return Const(in w);
    }

    private static Node Const(in UInt256 v) => new(KConst, 0, in v, null, null, "C" + v.ToString(CultureInfo.InvariantCulture));

    private static UInt256 Word(Address? a)
    {
        if (a is null) return Zero;
        Span<byte> buf = stackalloc byte[32];
        buf.Clear();
        a.Bytes.CopyTo(buf[12..]);
        return new UInt256(buf, true);
    }

    private static (long, long, long) ReadSched()
    {
        try
        {
            string[] p = File.ReadAllText("/proc/thread-self/schedstat").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (long.Parse(p[0], CultureInfo.InvariantCulture), long.Parse(p[1], CultureInfo.InvariantCulture), long.Parse(p[2], CultureInfo.InvariantCulture));
        }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    private static (long, long) ReadCs()
    {
        try
        {
            long v = 0, iv = 0;
            foreach (string line in File.ReadAllLines("/proc/thread-self/status"))
            {
                if (line.StartsWith("voluntary_ctxt_switches:", StringComparison.Ordinal)) v = long.Parse(line.AsSpan(24).Trim(), CultureInfo.InvariantCulture);
                else if (line.StartsWith("nonvoluntary_ctxt_switches:", StringComparison.Ordinal)) iv = long.Parse(line.AsSpan(27).Trim(), CultureInfo.InvariantCulture);
            }
            return (v, iv);
        }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }
}
