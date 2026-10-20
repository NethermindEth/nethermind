// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Logging;

namespace Nethermind.IlEvm;

/// <summary>Times every contract's frame parts on the path each took, for <see cref="IlEvmSettings.Stats"/>, and logs the per-contract totals at shutdown.</summary>
/// <remarks>
/// A part is a frame from its entry, or from where it resumes after a nested call, up to its next nested call or its end;
/// it keeps what an opcode does without a frame, as a cache-missing state read or a precompile's STATICCALL. Two runs over
/// the same blocks, with nothing and with the candidates compiled, give each contract's time on either path under the same
/// load. A part's time leaves out the GC pauses it waited for; what no collection explains stays, as a preemption or a
/// cold read, so each bucket keeps its longest part and each path tallies its slow parts apart. An interpreted or
/// handed-back part's window takes in the core hook's few instructions around the dispatch loop, which a compiled part's
/// does not; the smallest buckets measure that. Each thread counts into its own table, taking no lock or interlocked
/// operation but for its first part of a contract and, on block processing, a contract's first part of each block.
/// </remarks>
public static class IlEvmStats
{
    public enum RunPath : byte
    {
        /// <summary>No compiled code ran: the interpreter ran the whole part. Logged as I.</summary>
        Interpreted,
        /// <summary>Compiled code ran the part from start to finish. Logged as C.</summary>
        Compiled,
        /// <summary>Compiled code ran, then handed back, and the interpreter finished the part; its time and opcodes are those of both. Logged as H.</summary>
        HandedBack,
    }

    public enum ThreadKind : byte
    {
        /// <summary>Block processing, as <see cref="ProcessingThread.IsBlockProcessingThread"/> marks its threads for each processing pass. Logged as M.</summary>
        Main,
        /// <summary>Any other: prewarming, read-only queries, tests. Logged as O.</summary>
        Other,
    }

    /// <summary>The opcode-count buckets: bucket b holds the parts of 2^b to 2^(b+1)-1 opcodes, the first also those of none, the last all from 2^15.</summary>
    public const int Buckets = 16;

    /// <summary>A part is slow past this many nanoseconds per opcode, about a hundred times what an opcode takes: what it waited for was not its opcodes.</summary>
    public const int SlowNanosecondsPerOpcode = 500;

    /// <summary>The prefix of the dump's lines, which no other log line of the plugin starts with.</summary>
    public const string LinePrefix = "IL-EVM stats";

    /// <summary>A bucket's counters: parts, opcodes, ticks, and the longest part's ticks.</summary>
    private const int BucketSlots = 4;

    /// <summary>After the buckets, the slow parts' counters: parts, opcodes and ticks.</summary>
    private const int SlowSlot = Buckets * BucketSlots;

    private const int Slots = SlowSlot + 3;

    private const int Combos = 6;

    private static readonly string[] _comboNames = ["I.M", "I.O", "C.M", "C.O", "H.M", "H.O"];

    private static readonly long _slowTicksPerOpcode = Math.Max(1, SlowNanosecondsPerOpcode * Stopwatch.Frequency / 1_000_000_000);

    private static readonly double _ticksPerPauseTick = (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond;

    /// <summary>What every thread's entry for one code hash shares: the distinct blocks block processing ran the code in.</summary>
    internal sealed class Shared
    {
        /// <summary>The latest block counted; it only moves forward.</summary>
        public int LastBlock;
        public int Blocks;
    }

    /// <summary>One thread's counts for one code hash; only that thread writes them.</summary>
    internal sealed class LocalStats(ValueHash256 hash, int size, Shared shared)
    {
        public readonly ValueHash256 Hash = hash;
        public readonly int Size = size;
        public readonly Shared Shared = shared;
        public long Entries;
        public long HandedBackCompiledOps;
        /// <summary>The block this entry last counted toward <see cref="Shared.Blocks"/>.</summary>
        public int LastBlock;
        public readonly long[]?[] Counters = new long[]?[Combos];
    }

    private sealed class ThreadTable(int generation)
    {
        public readonly int Generation = generation;
        /// <summary>The counts by code hash. The owner locks it only to add an entry, and the dump to copy them.</summary>
        public readonly Dictionary<ValueHash256, LocalStats> Entries = [];
        /// <summary>The entries of code no cache stamped with its hash, as from a state override, so each code info is hashed once; weak, so it keeps no code alive.</summary>
        public ConditionalWeakTable<CodeInfo, LocalStats>? Unstamped;
        /// <summary>The entry counted last, which a loop's next call of the same code meets again; the only code info the table holds.</summary>
        public CodeInfo? LastCode;
        public LocalStats? Last;

        /// <summary>The part the interpreter is running: whose, since when, the GC's pause then, on which path, and how many opcodes compiled code ran of it.</summary>
        public CodeInfo? PendingCode;
        public long PendingStart;
        public long PendingPaused;
        public int PendingCompiledOps;
        public RunPath PendingPath;
        public bool PendingEntry;
        public bool PendingCount;

        public long PausedParts;
        public long PausedTicks;
    }

    [ThreadStatic] private static ThreadTable? _thread;

    /// <summary>Every thread's table, for the dump; a table outlives its thread here.</summary>
    private static readonly ConcurrentQueue<ThreadTable> _tables = new();
    private static readonly ConcurrentDictionary<ValueHash256, Shared> _shared = new();
    /// <summary>Bumped by <see cref="Reset"/>: a table of an older generation is dropped, and its thread starts a new one.</summary>
    private static int _generation;
    private static int _blocks;
    private static int _dumped;

    /// <summary>A block starts: block processing's parts from now on count toward it in each code's distinct blocks.</summary>
    public static int BlockStarted() => Interlocked.Increment(ref _blocks);

    public static int Blocks => Volatile.Read(ref _blocks);

    /// <summary>The GC's total pause so far, in <see cref="TimeSpan"/> ticks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PausedTicks() => GC.GetTotalPauseDuration().Ticks;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ThreadTable Table() => _thread is { } table && table.Generation == _generation ? table : NewTable();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadTable NewTable()
    {
        ThreadTable table = new(Volatile.Read(ref _generation));
        _tables.Enqueue(table);
        _thread = table;
        return table;
    }

    /// <summary>Starts timing a part the interpreter runs alone; the runner declines it right after, asking for its opcode count.</summary>
    /// <param name="count">Whether the frame counts toward its code's hotness, which <see cref="TryEnd"/> hands back.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BeginInterpreted(CodeInfo codeInfo, bool entry, bool count)
    {
        ThreadTable table = Table();
        table.PendingCode = codeInfo;
        table.PendingPath = RunPath.Interpreted;
        table.PendingEntry = entry;
        table.PendingCount = count;
        table.PendingCompiledOps = 0;
        table.PendingPaused = PausedTicks();
        // Last, so the part's time starts as close to the interpreter as the runner gets.
        table.PendingStart = Stopwatch.GetTimestamp();
    }

    /// <summary>Goes on timing a part compiled code handed back, from <paramref name="start"/>, before compiled code ran; the interpreter finishes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BeginHandedBack(CodeInfo codeInfo, bool entry, long start, long paused, int compiledOps)
    {
        ThreadTable table = Table();
        table.PendingCode = codeInfo;
        table.PendingPath = RunPath.HandedBack;
        table.PendingEntry = entry;
        // Compiled code's frames never feed hotness: the production runner does not report a hand-back's opcodes either.
        table.PendingCount = false;
        table.PendingCompiledOps = compiledOps;
        table.PendingPaused = paused;
        table.PendingStart = start;
    }

    /// <summary>Ends the pending part of <paramref name="codeInfo"/>, of which the interpreter ran <paramref name="opCodes"/> opcodes, at <paramref name="end"/>, and counts it.</summary>
    /// <returns>False when no part of <paramref name="codeInfo"/> was pending; a pending part of other code, whose interpreter run threw, is dropped.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TryEnd(CodeInfo codeInfo, int opCodes, long end, out bool forwardHotness)
    {
        long paused = PausedTicks();
        forwardHotness = false;
        ThreadTable? table = _thread;
        if (table?.PendingCode is not { } pending)
            return false;
        table.PendingCode = null;
        if (!ReferenceEquals(pending, codeInfo) || table.Generation != _generation)
            return false;
        forwardHotness = table.PendingCount;
        int compiledOps = table.PendingCompiledOps;
        Count(table, codeInfo, table.PendingPath, table.PendingEntry, compiledOps + opCodes, end - table.PendingStart, paused - table.PendingPaused, compiledOps);
        return true;
    }

    /// <summary>Counts a part compiled code ran from start to finish, in <paramref name="ticks"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void RecordCompiled(CodeInfo codeInfo, bool entry, int opCodes, long ticks, long paused)
    {
        long pausedSince = PausedTicks() - paused;
        ThreadTable table = Table();
        // A thread's parts follow one another: one still pending is one whose interpreter run threw.
        table.PendingCode = null;
        Count(table, codeInfo, RunPath.Compiled, entry, opCodes, ticks, pausedSince, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Count(ThreadTable table, CodeInfo codeInfo, RunPath path, bool entry, int opCodes, long ticks, long paused, int compiledOps)
    {
        if (paused != 0)
            ticks = WithoutPause(table, ticks, paused);
        LocalStats stats = ReferenceEquals(table.LastCode, codeInfo) ? table.Last! : Lookup(table, codeInfo);
        if (entry)
            stats.Entries++;
        if (compiledOps != 0)
            stats.HandedBackCompiledOps += compiledOps;

        bool main = ProcessingThread.IsBlockProcessingThread;
        if (main)
        {
            int block = Volatile.Read(ref _blocks);
            if (stats.LastBlock != block)
            {
                stats.LastBlock = block;
                CountBlock(stats.Shared, block);
            }
        }

        int combo = (int)path * 2 + (main ? (int)ThreadKind.Main : (int)ThreadKind.Other);
        long[] counters = stats.Counters[combo] ?? Allocate(stats, combo);
        int slot = Math.Min(BitOperations.Log2((uint)opCodes), Buckets - 1) * BucketSlots;
        counters[slot]++;
        counters[slot + 1] += opCodes;
        counters[slot + 2] += ticks;
        if (ticks > counters[slot + 3])
            counters[slot + 3] = ticks;
        if (ticks > _slowTicksPerOpcode * Math.Max(opCodes, 1))
        {
            counters[SlowSlot]++;
            counters[SlowSlot + 1] += opCodes;
            counters[SlowSlot + 2] += ticks;
        }
    }

    /// <summary>A part's <paramref name="ticks"/> less the collections it waited for, <paramref name="paused"/> in <see cref="TimeSpan"/> ticks; a pause that began before the part can make it no time, never less.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long WithoutPause(ThreadTable table, long ticks, long paused)
    {
        long left = Math.Max(0, ticks - (long)(paused * _ticksPerPauseTick));
        table.PausedParts++;
        table.PausedTicks += ticks - left;
        return left;
    }

    private static LocalStats Lookup(ThreadTable table, CodeInfo codeInfo)
    {
        ValueHash256 hash = codeInfo.CodeHash;
        LocalStats stats = hash != default ? ByHash(table, hash, codeInfo.CodeSpan.Length) : Unstamped(table, codeInfo);
        table.LastCode = codeInfo;
        table.Last = stats;
        return stats;
    }

    private static LocalStats ByHash(ThreadTable table, in ValueHash256 hash, int size) =>
        table.Entries.TryGetValue(hash, out LocalStats? stats) ? stats : Add(table, hash, size);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalStats Add(ThreadTable table, in ValueHash256 hash, int size)
    {
        LocalStats stats = new(hash, size, _shared.GetOrAdd(hash, static _ => new Shared()));
        lock (table.Entries)
            table.Entries.Add(hash, stats);
        return stats;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalStats Unstamped(ThreadTable table, CodeInfo codeInfo)
    {
        ConditionalWeakTable<CodeInfo, LocalStats> unstamped = table.Unstamped ??= new();
        if (unstamped.TryGetValue(codeInfo, out LocalStats? stats))
            return stats;
        stats = ByHash(table, ValueKeccak.Compute(codeInfo.CodeSpan), codeInfo.CodeSpan.Length);
        unstamped.AddOrUpdate(codeInfo, stats);
        return stats;
    }

    /// <summary>Counts <paramref name="block"/> toward a code's distinct blocks once, however many threads and entries run the code in it; a block older than the latest one counted is not counted again.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool CountBlock(Shared shared, int block)
    {
        int last;
        while ((last = Volatile.Read(ref shared.LastBlock)) < block)
        {
            if (Interlocked.CompareExchange(ref shared.LastBlock, block, last) == last)
            {
                Interlocked.Increment(ref shared.Blocks);
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long[] Allocate(LocalStats stats, int combo)
    {
        long[] counters = new long[Slots];
        // The dump may read the slot from another thread at any time.
        Volatile.Write(ref stats.Counters[combo], counters);
        return counters;
    }

    /// <summary>One contract's counts over every thread that ran it, as its line of the dump gives them.</summary>
    public sealed class ContractTotals
    {
        private readonly long[]?[] _counters = new long[]?[Combos];

        internal ContractTotals(ValueHash256 hash, int size, int blocks, bool shipped)
        {
            Hash = hash;
            Size = size;
            Blocks = blocks;
            Shipped = shipped;
        }

        public ValueHash256 Hash { get; }

        public int Size { get; }

        /// <summary>The distinct blocks block processing ran the code in.</summary>
        public int Blocks { get; }

        /// <summary>Whether the startup compile published a contract for the code.</summary>
        public bool Shipped { get; }

        /// <summary>Parts that started the frame, at program counter 0: the frames entered.</summary>
        public long Entries { get; private set; }

        /// <summary>Of the handed-back parts' opcodes, those compiled code ran before it handed back.</summary>
        public long HandedBackCompiledOps { get; private set; }

        public long Ops { get; private set; }

        /// <summary><see cref="Stopwatch"/> ticks over every path and thread, the collections waited for left out.</summary>
        public long Ticks { get; private set; }

        /// <summary>Whether compiled code ran any of its parts, to their end or until it handed back.</summary>
        public bool RanCompiled => _counters[Combo(RunPath.Compiled, ThreadKind.Main)] is not null || _counters[Combo(RunPath.Compiled, ThreadKind.Other)] is not null ||
            _counters[Combo(RunPath.HandedBack, ThreadKind.Main)] is not null || _counters[Combo(RunPath.HandedBack, ThreadKind.Other)] is not null;

        public (long Parts, long Ops, long Ticks) Of(RunPath path, ThreadKind thread) => Of(Combo(path, thread));

        public (long Parts, long Ops, long Ticks) Of(RunPath path, ThreadKind thread, int bucket) => Of(Combo(path, thread), bucket);

        public long LongestTicks(RunPath path, ThreadKind thread, int bucket) => LongestTicks(Combo(path, thread), bucket);

        /// <summary>The parts, opcodes and ticks of the slow parts on <paramref name="path"/> and <paramref name="thread"/>, which <see cref="Of(RunPath, ThreadKind)"/> includes.</summary>
        public (long Parts, long Ops, long Ticks) Slow(RunPath path, ThreadKind thread) => Slow(Combo(path, thread));

        internal (long Parts, long Ops, long Ticks) Of(int combo)
        {
            long parts = 0, ops = 0, ticks = 0;
            if (_counters[combo] is { } counters)
            {
                for (int slot = 0; slot < SlowSlot; slot += BucketSlots)
                {
                    parts += counters[slot];
                    ops += counters[slot + 1];
                    ticks += counters[slot + 2];
                }
            }

            return (parts, ops, ticks);
        }

        internal (long Parts, long Ops, long Ticks) Of(int combo, int bucket) =>
            _counters[combo] is { } counters ? (counters[bucket * BucketSlots], counters[bucket * BucketSlots + 1], counters[bucket * BucketSlots + 2]) : default;

        internal long LongestTicks(int combo, int bucket) => _counters[combo] is { } counters ? counters[bucket * BucketSlots + 3] : 0;

        internal (long Parts, long Ops, long Ticks) Slow(int combo) =>
            _counters[combo] is { } counters ? (counters[SlowSlot], counters[SlowSlot + 1], counters[SlowSlot + 2]) : default;

        private static int Combo(RunPath path, ThreadKind thread) => (int)path * 2 + (int)thread;

        /// <summary>Adds one thread's entry; its owner may still be writing, so the counts are as of some moment during the copy.</summary>
        internal void Add(LocalStats stats)
        {
            Entries += Volatile.Read(ref stats.Entries);
            HandedBackCompiledOps += Volatile.Read(ref stats.HandedBackCompiledOps);
            for (int combo = 0; combo < Combos; combo++)
            {
                if (Volatile.Read(ref stats.Counters[combo]) is { } source)
                    Merge(combo, source);
            }
        }

        internal void Add(ContractTotals other)
        {
            Entries += other.Entries;
            HandedBackCompiledOps += other.HandedBackCompiledOps;
            for (int combo = 0; combo < Combos; combo++)
            {
                if (other._counters[combo] is { } source)
                    Merge(combo, source);
            }
        }

        private void Merge(int combo, long[] source)
        {
            long[] target = _counters[combo] ??= new long[Slots];
            for (int slot = 0; slot < Slots; slot++)
            {
                long value = Volatile.Read(ref source[slot]);
                int counter = slot < SlowSlot ? slot % BucketSlots : -1;
                if (counter == 3)
                {
                    target[slot] = Math.Max(target[slot], value);
                    continue;
                }

                target[slot] += value;
                if (counter == 1)
                    Ops += value;
                else if (counter == 2)
                    Ticks += value;
            }
        }
    }

    /// <summary>The bookkeeping beside the contracts, for the dump's header.</summary>
    /// <param name="PausedTicks">The <see cref="Stopwatch"/> ticks taken off the parts that waited for a collection.</param>
    /// <param name="Entries">The tables' entries: one per contract and thread that ran it.</param>
    public readonly record struct TableTotals(long PausedParts, long PausedTicks, int Entries, int Threads);

    public static TableTotals Tables()
    {
        long parts = 0, ticks = 0;
        int entries = 0, threads = 0;
        foreach (ThreadTable table in _tables)
        {
            threads++;
            parts += Volatile.Read(ref table.PausedParts);
            ticks += Volatile.Read(ref table.PausedTicks);
            lock (table.Entries)
                entries += table.Entries.Count;
        }

        return new TableTotals(parts, ticks, entries, threads);
    }

    /// <summary>Every contract counted so far, merged over threads by code hash, the most time first.</summary>
    /// <remarks>Safe while threads still run: each table is copied under the lock its owner takes only to add an entry, and the counters are read as they are.</remarks>
    public static List<ContractTotals> Snapshot()
    {
        Dictionary<ValueHash256, ContractTotals> merged = [];
        foreach (ThreadTable table in _tables)
        {
            LocalStats[] entries;
            lock (table.Entries)
                entries = [.. table.Entries.Values];
            foreach (LocalStats stats in entries)
            {
                if (stats.Size == 0)
                    continue;
                if (!merged.TryGetValue(stats.Hash, out ContractTotals? totals))
                    merged[stats.Hash] = totals = new ContractTotals(stats.Hash, stats.Size, Volatile.Read(ref stats.Shared.Blocks), IlEvm.IsPrecompiled(stats.Hash));
                totals.Add(stats);
            }
        }

        return [.. merged.Values.OrderByDescending(static c => c.Ticks).ThenByDescending(static c => c.Ops)];
    }

    /// <summary>Logs the <paramref name="top"/> contracts with the most time once, at shutdown, and every other one compiled code ran, so that no shipped contract falls below the cut.</summary>
    public static void Dump(ILogger logger, int top)
    {
        if (Interlocked.Exchange(ref _dumped, 1) == 1)
            return;
        List<ContractTotals> contracts = Snapshot();
        // Console as well as the log: a shutting-down logger may already have stopped writing.
        void Write(string line)
        {
            if (logger.IsInfo)
                logger.Info(line);
            else
                Console.WriteLine(line);
        }

        Write(Header(LinePrefix, contracts, Tables()));
        for (int i = 0; i < contracts.Count; i++)
        {
            if (i < top || contracts[i].RanCompiled)
                Write(Describe(LinePrefix, i + 1, contracts[i]));
        }

        if (IlEvmSettings.Profile)
        {
            // The profile's own lines, so the list tooling reads a stats run's log as a profile's; profiling compiles nothing.
            List<ContractTotals> busiest = [.. contracts.OrderByDescending(static c => c.Ops)];
            long totalOps = busiest.Sum(static c => c.Ops);
            Write(string.Create(CultureInfo.InvariantCulture,
                $"IL-EVM profile: {busiest.Count} contracts, {busiest.Sum(static c => c.Entries)} frames, {totalOps} interpreted opcodes"));
            long cumulative = 0;
            for (int i = 0; i < Math.Min(top, busiest.Count); i++)
            {
                ContractTotals contract = busiest[i];
                cumulative += contract.Ops;
                Write(string.Create(CultureInfo.InvariantCulture,
                    $"IL-EVM profile {i + 1} {contract.Hash} ops={contract.Ops} frames={contract.Entries} size={contract.Size} cum={(totalOps == 0 ? 0 : (double)cumulative / totalOps):F4}"));
            }

            Write("IL-EVM profile end");
        }

        Write(LinePrefix + " end");
    }

    /// <summary>The header: the totals over every contract, then what says whether two runs compare.</summary>
    private static string Header(string prefix, List<ContractTotals> contracts, TableTotals tables)
    {
        ContractTotals all = new(default, 0, 0, false);
        foreach (ContractTotals contract in contracts)
            all.Add(contract);

        StringBuilder line = new();
        line.Append(CultureInfo.InvariantCulture, $"{prefix}: {contracts.Count} contracts, {Blocks} blocks, totals");
        for (int combo = 0; combo < Combos; combo++)
            AppendCounts(line, _comboNames[combo], all.Of(combo));
        for (int combo = 0; combo < Combos; combo++)
            AppendCounts(line, "s" + _comboNames[combo], all.Slow(combo));
        AppendBuckets(line, all);
        line.Append(CultureInfo.InvariantCulture,
            $" gc={tables.PausedParts}/{Nanoseconds(tables.PausedTicks)} runtime-compiled={IlEvmSettings.CompiledContracts}" +
            $" handoffs={Nethermind.Blockchain.Metrics.PrewarmHandoffs}/{Nethermind.Blockchain.Metrics.PrewarmHandoffsRejected}/{Nethermind.Blockchain.Metrics.PrewarmHandoffsMissing}" +
            $" tables={tables.Entries}/{tables.Threads} heapMiB={GC.GetTotalMemory(false) >> 20} {IlSurfaceCounts.StatsToken()}");
        return line.ToString();
    }

    private static string Describe(string prefix, int rank, ContractTotals contract)
    {
        StringBuilder line = new();
        line.Append(CultureInfo.InvariantCulture,
            $"{prefix} {rank} {contract.Hash} size={contract.Size} entries={contract.Entries} blocks={contract.Blocks} shipped={(contract.Shipped ? 1 : 0)}");
        for (int combo = 0; combo < Combos; combo++)
        {
            if (contract.Of(combo) is { Parts: > 0 } counts)
                AppendCounts(line, _comboNames[combo], counts);
        }

        for (int combo = 0; combo < Combos; combo++)
        {
            if (contract.Slow(combo) is { Parts: > 0 } slow)
                AppendCounts(line, "s" + _comboNames[combo], slow);
        }

        AppendBuckets(line, contract);
        return line.ToString();
    }

    private static void AppendCounts(StringBuilder line, string name, (long Parts, long Ops, long Ticks) counts) =>
        line.Append(CultureInfo.InvariantCulture, $" {name}={counts.Parts}/{counts.Ops}/{Nanoseconds(counts.Ticks)}");

    private static void AppendBuckets(StringBuilder line, ContractTotals totals)
    {
        for (int combo = 0; combo < Combos; combo++)
        {
            if (totals.Of(combo).Parts == 0)
                continue;
            line.Append(" h").Append(_comboNames[combo]).Append('=');
            bool first = true;
            for (int bucket = 0; bucket < Buckets; bucket++)
            {
                (long parts, long ops, long ticks) = totals.Of(combo, bucket);
                if (parts == 0)
                    continue;
                if (!first)
                    line.Append(',');
                first = false;
                line.Append(CultureInfo.InvariantCulture, $"{bucket}:{parts}/{ops}/{Nanoseconds(ticks)}/{Nanoseconds(totals.LongestTicks(combo, bucket))}");
            }
        }
    }

    /// <summary>Logs the totals so far as <c>IL-EVM stats@&lt;blocks&gt;</c> lines, which tooling that reads the shutdown dump's <c>IL-EVM stats </c> lines never sees.</summary>
    /// <remarks>Called off the block-processing thread; safe while threads still count, as <see cref="Snapshot"/> is.</remarks>
    public static void DumpSnapshot(ILogger logger, int top, int blocks)
    {
        string prefix = string.Create(CultureInfo.InvariantCulture, $"{LinePrefix}@{blocks}");
        List<ContractTotals> contracts = Snapshot();
        void Write(string line)
        {
            if (logger.IsInfo)
                logger.Info(line);
            else
                Console.WriteLine(line);
        }

        Write(Header(prefix, contracts, Tables()));
        for (int i = 0; i < contracts.Count; i++)
        {
            if (i < top || contracts[i].RanCompiled)
                Write(Describe(prefix, i + 1, contracts[i]));
        }

        Write(prefix + " end");
    }

    private static readonly double _nanosecondsPerTick = 1e9 / Stopwatch.Frequency;

    /// <summary><paramref name="ticks"/> of <see cref="Stopwatch"/> in whole nanoseconds.</summary>
    public static long Nanoseconds(long ticks) => (long)(ticks * _nanosecondsPerTick);

    /// <summary>Forgets everything counted, for tests; a part pending on any thread is dropped.</summary>
    internal static void Reset()
    {
        Interlocked.Increment(ref _generation);
        _tables.Clear();
        _shared.Clear();
        Volatile.Write(ref _blocks, 0);
        Volatile.Write(ref _dumped, 0);
    }
}

/// <summary>The frame runner of a <see cref="IlEvmSettings.Stats"/> run: runs frames as the production runner does but for the self-check, which is off, and times each part on the path it takes.</summary>
/// <remarks>
/// A runner of its own, attached only for a measurement run, so the production runner carries no trace of the timing. A
/// part the interpreter runs, alone or after a hand-back, is declined asking for its opcode count and timed until
/// <see cref="OnInterpreted"/>. Initcode is declined first, as by the production runner: never counted, compiled or timed.
/// A query's code is looked up as the production runner looks it up, and the trace runner takes the temperature gate's
/// samples and verdicts as the production one does, so a run times the contracts production would run compiled. Block
/// processing runs compiled only the shipped contracts, so that two runs differ only by their lists.
/// </remarks>
public sealed class IlEvmTimedFrameRunner : FrameCodeRunner<EthereumGasPolicy>
{
    /// <summary>The runner for the read-only call environments: compiles hot code at run time, as the production one does.</summary>
    public static IlEvmTimedFrameRunner Instance { get; } = new(IlEvmSurface.Call);

    /// <summary>As <see cref="Instance"/>, for the replay and traced-call environments.</summary>
    public static IlEvmTimedFrameRunner TraceInstance { get; } = new(IlEvmSurface.Trace);

    /// <summary>As <see cref="Instance"/>, for the simulation environments.</summary>
    public static IlEvmTimedFrameRunner SimulateInstance { get; } = new(IlEvmSurface.Simulate);

    /// <summary>The runner for block processing (Scope=All): runs the shipped contracts compiled, and compiles at run time only as <see cref="IlEvmSettings.BlockRuntimeCompile"/> says.</summary>
    public static IlEvmTimedFrameRunner Block { get; } = new(IlEvmSurface.Blocks);

    public static IlEvmTimedFrameRunner For(IlEvmSurface surface) => surface switch
    {
        IlEvmSurface.Call => Instance,
        IlEvmSurface.Trace => TraceInstance,
        IlEvmSurface.Simulate => SimulateInstance,
        _ => Block,
    };

    private readonly bool _block;

    public IlEvmSurface Surface { get; }

    private IlEvmTimedFrameRunner(IlEvmSurface surface)
    {
        Surface = surface;
        _block = surface == IlEvmSurface.Blocks;
    }

    // Optimized from the first call, as the production runner: a tier-0 copy would time the runner, not the frames.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        long[] counts = IlSurfaceCounts.OfThisThread();
        int surface = (int)Surface;
        // A temperature sample is due: only the trace runner's clock runs, while the gate is on.
        bool sample = ++counts[IlSurfaceCounts.Offered + surface] >= counts[IlSurfaceCounts.SampleAt + surface] && StartSample(counts, surface);
        int before = vm.OpCodeCount;
        EvmExceptionType result = RunPart(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);
        int compiledOps = vm.OpCodeCount - before;
        if (compiledOps != 0)
            counts[IlSurfaceCounts.Compiled + surface]++;
        return sample ? EndSample(codeInfo, result, compiledOps) : result;
    }

    /// <summary>Sets this thread's next sample as the production trace runner does; false if the gate was turned off since its clock was set.</summary>
    private bool StartSample(long[] counts, int surface)
    {
        if (!IlTemperature.On || Surface != IlEvmSurface.Trace)
        {
            counts[IlSurfaceCounts.SampleAt + surface] = long.MaxValue;
            return false;
        }

        counts[IlSurfaceCounts.SampleAt + surface] = counts[IlSurfaceCounts.Offered + surface] + IlTemperature.NextGap();
        IlTemperature.Roll();
        return true;
    }

    /// <summary>Counts a sampled part toward its code's temperature: the opcodes compiled code ran now, the interpreter's once it reports them.</summary>
    private static EvmExceptionType EndSample(CodeInfo codeInfo, EvmExceptionType result, int compiledOps)
    {
        if (CodeState(codeInfo) is not IlEvm.IlCodeState state)
            return result;
        if (result == Declined)
        {
            IlTemperature.Mark(codeInfo, state, compiledOps, hotness: false);
            return DeclinedReportingOpcodes;
        }

        if (result == DeclinedReportingOpcodes)
            IlTemperature.Mark(codeInfo, state, compiledOps, hotness: true);
        else
            IlTemperature.Add(state, compiledOps);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EvmExceptionType RunPart(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        if (!IlEvmSettings.Enabled)
            return Declined;
        if (vm.VmState.ExecutionType.IsAnyCreate())
            return Declined;

        bool entry = programCounter == 0;
        IlEvm.IlContract? contract = null;
        bool count = false;
        if (IlEvmSettings.Profile)
        {
            // Nothing is compiled while profiling; the stats replace the profile's counters, and their dump logs its lines.
        }
        else if (_block && !IlEvmSettings.BlockRuntimeCompile)
        {
            contract = IlEvm.FindShippedOnly(codeInfo, bodies);
        }
        else
        {
            bool gated = Surface == IlEvmSurface.Trace && IlTemperature.On;
            contract = IlEvm.Find(codeInfo, bodies, out IlEvm.IlCodeState state, out count, query: _block ? null : vm, gated: gated);
            // The temperature gate: a contract that is not hot in traces runs on the interpreter there.
            if (contract is not null && gated)
            {
                bool hot = IlTemperature.IsHot(codeInfo, state);
                IlSurfaceCounts.OfThisThread()[hot ? IlSurfaceCounts.HotDecisions : IlSurfaceCounts.ColdDecisions]++;
                if (!hot)
                    contract = null;
            }
        }

        if (contract is null)
        {
            IlEvmStats.BeginInterpreted(codeInfo, entry, count);
            return DeclinedReportingOpcodes;
        }

        bool cancelable = (flags & FrameRunFlags.Cancelable) != 0;
        IlEvmSettings.SawFrame();
        int before = vm.OpCodeCount;
        long paused = IlEvmStats.PausedTicks();
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        if (result != Declined)
        {
            long ticks = Stopwatch.GetTimestamp() - start;
            IlEvmStats.RecordCompiled(codeInfo, entry, vm.OpCodeCount - before, ticks, paused);
            return result;
        }

        // The interpreter finishes what compiled code handed back, timed from before compiled code ran.
        IlEvmStats.BeginHandedBack(codeInfo, entry, start, paused, vm.OpCodeCount - before);
        return DeclinedReportingOpcodes;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override void OnInterpreted(CodeInfo codeInfo, int opCodes)
    {
        // First, so the part's time ends as close to the interpreter as the runner gets.
        long end = Stopwatch.GetTimestamp();
        // A temperature sample's interpreted opcodes; a part declined only to be measured counts nowhere else.
        if (IlTemperature.Finish(codeInfo, opCodes))
            return;
        // Only frames the lookup counted feed hotness, as with the production runner, where the others never report.
        if (IlEvmStats.TryEnd(codeInfo, opCodes, end, out bool forwardHotness) && forwardHotness)
            IlEvm.OnInterpreted(codeInfo, opCodes);
    }
}
