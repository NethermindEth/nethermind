// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.Core.Diagnostics;

/// <summary>Layer that terminated a main-thread state read, ordered from shallowest to deepest.</summary>
public enum ReadTraceSource : byte
{
    Unknown = 0,
    IntraTx = 1,        // per-transaction cache (StateProvider._intraTxCache / storage change stack)
    BlockDict = 2,      // per-block dictionary (StateProvider._blockChanges / PerContractState.BlockChange)
    PreBlockCache = 3,  // PreBlockCaches SeqlockCache (prewarmer-shared)
    BundleWriteBuf = 4, // SnapshotBundle._changedAccounts/_changedSlots (this block's writes + account read-backfill)
    BundleSnapshot = 5, // bundle-local snapshots (multi-block branch scope only)
    SnapshotWindow = 6, // ReadOnlySnapshotBundle backward scan (recent unpersisted blocks in memory)
    CarryForward = 7,   // CarryForwardCachingPersistence (FlatDb cross-block read cache)
    RocksDb = 8,        // native RocksDB get
}

/// <summary>Who populated a PreBlockCaches entry that a main-thread read later hit.</summary>
public enum ReadTraceProvenance : byte
{
    None = 0,
    Prewarmer = 1,    // prewarmer env miss-backfill (isPrewarmer: true)
    MainBackfill = 2, // main-scope miss-backfill earlier in the same block (isPrewarmer: false)
    Bal = 3,          // BAL HintBal CacheSink
}

/// <summary>
/// Diagnostic per-read provenance tracer for main-thread state access.
/// </summary>
/// <remarks>
/// Emits one event per account/slot ask on the main block-processing thread, attributed to the
/// exact cache layer that served it, and dumps them as CSV.GZ for offline analysis. Enabled only
/// when <c>Blocks.ReadTraceOutput</c> is set; every hook is a no-op behind a single static bool
/// otherwise. Recording is deferred (preallocated buffer, background writer) so the trace does not
/// serialize the traced threads. Thread model: block/tx bracketing and read events come from the
/// single main processing thread (guarded by a [ThreadStatic] armed flag, so concurrent prewarmer
/// traffic through the same layers is ignored); the PreBlockCaches provenance map is written from
/// any thread and read at event-emit time.
/// </remarks>
public static class ReadTrace
{
    private const int BufferCapacity = 1 << 20; // 1M events/block; overflow counted, not resized

    private struct Ev
    {
        public Address? Addr;
        public UInt256 Slot;
        public long RocksTicks;
        public int Tx;
        public byte Kind; // 0 = account, 1 = slot
        public byte Source;
        public byte Prov;
    }

    private readonly struct BlockChunk(long block, int txCount, Ev[] buffer, int count, int dropped)
    {
        public readonly long Block = block;
        public readonly int TxCount = txCount;
        public readonly Ev[] Buffer = buffer;
        public readonly int Count = count;
        public readonly int Dropped = dropped;
    }

    private struct ReadContext
    {
        public Address? Addr;
        public UInt256 Slot;
        public long RocksTicks;
        public byte Kind;
        public byte Source;
    }

    public static bool Enabled { get; private set; }

    [ThreadStatic] private static bool _armed;

    // Only the armed (main processing) thread touches these between BeginBlock/EndBlock.
    private static Ev[]? _buffer;
    private static int _count;
    private static int _dropped;
    private static int _tx;
    private static long _blockNumber;
    private static int _txCount;
    private static int _depth;
    private static ReadContext[] _stack = new ReadContext[8];

    private static volatile bool _currentBlockTraced;
    private static long _provBlock = -1;
    private static readonly ConcurrentDictionary<AddressAsKey, byte> _accountProv = new();
    private static readonly ConcurrentDictionary<StorageCell, byte> _slotProv = new(StorageCell.EqualityComparer);
    private static readonly ConcurrentQueue<Ev[]> _bufferPool = new();

    private static Channel<BlockChunk>? _channel;
    private static Task? _writerTask;
    private static volatile bool _writerDead;
    private static long[]? _filterBlocks;
    private static long _filterFrom = -1, _filterTo = -1;
    private static int _filterFirstN = -1;
    private static int _tracedBlocks;

    /// <summary>Arms the tracer. Called once at startup when <c>Blocks.ReadTraceOutput</c> is configured.</summary>
    /// <param name="outputDir">Directory for readtrace-*.csv.gz and readtrace-blocks-*.csv.</param>
    /// <param name="blocksFilter">null/empty = every block; "first:N" = first N processed blocks; "A-B" = number range; or a comma-separated list of block numbers (forms combinable by comma).</param>
    public static void Configure(string outputDir, string? blocksFilter)
    {
        if (Enabled) return;
        Directory.CreateDirectory(outputDir);
        ParseFilter(blocksFilter);
        // Bounded: the armed thread blocks in EndBlock rather than queueing 56MB chunks unboundedly
        // (a trace run is an attribution run, not a timing run).
        _channel = Channel.CreateBounded<BlockChunk>(new BoundedChannelOptions(4) { SingleReader = true });
        _bufferPool.Enqueue(new Ev[BufferCapacity]);
        _bufferPool.Enqueue(new Ev[BufferCapacity]);
        _writerTask = Task.Run(async () =>
        {
            try
            {
                await RunWriter(outputDir);
            }
            catch (Exception e)
            {
                // Fail loudly: a dead writer would otherwise produce silently-empty trace files.
                _writerDead = true;
                File.WriteAllText(Path.Combine(outputDir, "readtrace-error.txt"), e.ToString());
            }
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _channel?.Writer.TryComplete();
            _writerTask?.Wait(TimeSpan.FromSeconds(10));
        };
        Enabled = true;
    }

    private static void ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return;
        List<long> singles = [];
        foreach (string raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("first:", StringComparison.OrdinalIgnoreCase))
            {
                _filterFirstN = int.Parse(raw["first:".Length..], CultureInfo.InvariantCulture);
            }
            else if (raw.IndexOf('-', 1) is int dash and > 0)
            {
                _filterFrom = long.Parse(raw[..dash], CultureInfo.InvariantCulture);
                _filterTo = long.Parse(raw[(dash + 1)..], CultureInfo.InvariantCulture);
            }
            else
            {
                singles.Add(long.Parse(raw, CultureInfo.InvariantCulture));
            }
        }

        if (singles.Count > 0) _filterBlocks = [.. singles];
    }

    private static bool ShouldTrace(long number)
    {
        if (_filterFirstN < 0 && _filterFrom < 0 && _filterBlocks is null) return true;
        if (_filterFirstN >= 0 && _tracedBlocks < _filterFirstN) return true;
        if (_filterFrom >= 0 && number >= _filterFrom && number <= _filterTo) return true;
        if (_filterBlocks is not null && Array.IndexOf(_filterBlocks, number) >= 0) return true;
        return false;
    }

    /// <summary>
    /// Opens the provenance-recording window for a block. Must be called on the main processing thread
    /// BEFORE the prewarmer is kicked off, so early prewarmer PreBlockCaches sets are attributed.
    /// </summary>
    public static void BeginProvenance(long number)
    {
        if (!Enabled || _writerDead || _armed) return;
        if (!ShouldTrace(number)) return;

        _accountProv.Clear();
        _slotProv.Clear();
        _provBlock = number;
        _currentBlockTraced = true;
    }

    /// <summary>Starts tracing a block on the calling (main processing) thread. No-op when disabled or filtered out.</summary>
    public static void BeginBlock(long number, int txCount)
    {
        if (!Enabled || _writerDead || _armed) return;
        if (!ShouldTrace(number)) return;

        _tracedBlocks++;
        if (!_bufferPool.TryDequeue(out Ev[]? buffer)) buffer = new Ev[BufferCapacity];
        _buffer = buffer;
        _count = 0;
        _dropped = 0;
        _tx = -1;
        _depth = 0;
        _blockNumber = number;
        _txCount = txCount;
        if (_provBlock != number)
        {
            // BeginProvenance was not called for this block (non-prewarmed path) — clear here instead.
            _accountProv.Clear();
            _slotProv.Clear();
        }

        _currentBlockTraced = true;
        _armed = true;
    }

    /// <summary>Sets the transaction index for subsequent events (-1 = pre-tx/system work, -2 = post-tx block work).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetTx(int txIndex)
    {
        if (_armed) _tx = txIndex;
    }

    /// <summary>Ends the traced block and hands its events to the background writer (blocking briefly if it is behind).</summary>
    public static void EndBlock()
    {
        if (!_armed) return;
        _armed = false;
        _currentBlockTraced = false;
        _provBlock = -1;
        Ev[] buffer = _buffer!;
        _buffer = null;
        BlockChunk chunk = new(_blockNumber, _txCount, buffer, _count, _dropped);
        while (!_writerDead && !_channel!.Writer.TryWrite(chunk))
        {
            System.Threading.Thread.Sleep(1);
        }
    }

    // ---- direct-hit events (shallow layers that both identify and serve the read) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AccountRead(Address address, ReadTraceSource source)
    {
        if (_armed) Emit(0, address, default, (byte)source, 0, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SlotRead(Address address, in UInt256 index, ReadTraceSource source)
    {
        if (_armed) Emit(1, address, in index, (byte)source, 0, 0);
    }

    // ---- deep reads: Begin -> (one layer calls Mark) -> End emits with deepest source ----

    /// <summary>True when the calling thread is inside a traced deep read (gates the RocksDB timing hook).</summary>
    public static bool InRead
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _armed && _depth > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginAccountRead(Address address)
    {
        if (_armed) Push(0, address, default);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginSlotRead(Address address, in UInt256 index)
    {
        if (_armed) Push(1, address, in index);
    }

    private static void Push(byte kind, Address address, in UInt256 slot)
    {
        // Keep Push/EndRead balanced past capacity: count the frame, emit nothing for it.
        if (_depth >= _stack.Length) { _depth++; _dropped++; return; }
        ref ReadContext ctx = ref _stack[_depth++];
        ctx.Kind = kind;
        ctx.Addr = address;
        ctx.Slot = slot;
        ctx.Source = (byte)ReadTraceSource.Unknown;
        ctx.RocksTicks = 0;
    }

    /// <summary>Records the layer that served the current deep read (deepest source wins).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mark(ReadTraceSource source)
    {
        if (!_armed || _depth == 0) return;
        ref ReadContext ctx = ref _stack[_depth - 1];
        if ((byte)source > ctx.Source) ctx.Source = (byte)source;
    }

    /// <summary>Records a native RocksDB get serving the current deep read, with its duration in Stopwatch ticks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MarkRocksDb(long elapsedTicks)
    {
        if (!_armed || _depth == 0) return;
        ref ReadContext ctx = ref _stack[_depth - 1];
        ctx.Source = (byte)ReadTraceSource.RocksDb;
        ctx.RocksTicks += elapsedTicks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndRead()
    {
        if (!_armed || _depth == 0) return;
        if (_depth > _stack.Length) { _depth--; return; }
        ref ReadContext ctx = ref _stack[--_depth];
        byte prov = 0;
        if (ctx.Source == (byte)ReadTraceSource.PreBlockCache)
        {
            if (ctx.Kind == 0) _accountProv.TryGetValue(ctx.Addr!, out prov);
            else _slotProv.TryGetValue(new StorageCell(ctx.Addr!, in ctx.Slot), out prov);
        }

        Emit(ctx.Kind, ctx.Addr!, in ctx.Slot, ctx.Source, prov, ctx.RocksTicks);
    }

    private static void Emit(byte kind, Address address, in UInt256 slot, byte source, byte prov, long rocksTicks)
    {
        Ev[]? buffer = _buffer;
        if (buffer is null) return;
        int i = _count;
        if ((uint)i >= (uint)buffer.Length) { _dropped++; return; }
        ref Ev e = ref buffer[i];
        e.Addr = address;
        e.Slot = slot;
        e.RocksTicks = rocksTicks;
        e.Tx = _tx;
        e.Kind = kind;
        e.Source = source;
        e.Prov = prov;
        _count = i + 1;
    }

    // ---- provenance map: called from ANY thread that populates PreBlockCaches ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnPreBlockAccountSet(Address address, ReadTraceProvenance provenance)
    {
        if (Enabled && _currentBlockTraced) _accountProv.TryAdd(address, (byte)provenance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnPreBlockSlotSet(in StorageCell cell, ReadTraceProvenance provenance)
    {
        if (Enabled && _currentBlockTraced) _slotProv.TryAdd(cell, (byte)provenance);
    }

    // ---- background writer ----

    private static readonly string[] _sourceNames = ["Unknown", "IntraTx", "BlockDict", "PreBlock", "WriteBuf", "BundleSnap", "SnapWindow", "CarryFwd", "RocksDb"];
    private static readonly string[] _provNames = ["", "pw", "mb", "bal"];

    private static async Task RunWriter(string outputDir)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        await using FileStream eventsFile = File.Create(Path.Combine(outputDir, $"readtrace-{stamp}.csv.gz"));
        await using GZipStream gzip = new(eventsFile, CompressionLevel.Fastest);
        await using StreamWriter events = new(gzip) { NewLine = "\n" };
        await using StreamWriter blocks = new(File.Create(Path.Combine(outputDir, $"readtrace-blocks-{stamp}.csv"))) { NewLine = "\n" };

        await events.WriteLineAsync("block,tx,seq,kind,layer,prov,address,slot,rocks_us");
        await blocks.WriteLineAsync("block,txs,kind,events,dropped,intratx,blockdict,preblock,preblock_pw,preblock_mb,preblock_bal,writebuf,bundlesnap,snapwindow,carryfwd,rocksdb,unknown,rocks_ms");

        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        ChannelReader<BlockChunk> reader = _channel!.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out BlockChunk chunk))
            {
                WriteChunk(events, blocks, in chunk, usPerTick);
                Array.Clear(chunk.Buffer, 0, chunk.Count);
                if (_bufferPool.Count < 4) _bufferPool.Enqueue(chunk.Buffer);
                await events.FlushAsync();
                await blocks.FlushAsync();
            }
        }
    }

    private static void WriteChunk(StreamWriter events, StreamWriter blocks, in BlockChunk chunk, double usPerTick)
    {
        // per-kind per-layer counters: [kind, source] plus provenance splits and rocks time
        Span<long> counters = stackalloc long[2 * 9];
        Span<long> provCounters = stackalloc long[2 * 4];
        Span<long> rocksTicks = stackalloc long[2];

        for (int i = 0; i < chunk.Count; i++)
        {
            ref Ev e = ref chunk.Buffer[i];
            counters[e.Kind * 9 + e.Source]++;
            if (e.Source == (byte)ReadTraceSource.PreBlockCache) provCounters[e.Kind * 4 + e.Prov]++;
            rocksTicks[e.Kind] += e.RocksTicks;

            events.Write(chunk.Block);
            events.Write(',');
            events.Write(e.Tx);
            events.Write(',');
            events.Write(i);
            events.Write(',');
            events.Write(e.Kind == 0 ? 'A' : 'S');
            events.Write(',');
            events.Write(_sourceNames[e.Source]);
            events.Write(',');
            events.Write(_provNames[e.Prov]);
            events.Write(',');
            events.Write(e.Addr?.ToString() ?? "");
            events.Write(',');
            if (e.Kind == 1) events.Write(e.Slot.ToString("x"));
            events.Write(',');
            if (e.RocksTicks > 0) events.Write((e.RocksTicks * usPerTick).ToString("F1", CultureInfo.InvariantCulture));
            events.Write('\n');
        }

        for (int kind = 0; kind < 2; kind++)
        {
            Span<long> c = counters.Slice(kind * 9, 9);
            Span<long> p = provCounters.Slice(kind * 4, 4);
            long total = 0;
            foreach (long v in c) total += v;
            // Dropped is a combined (A+S) counter — emitted on the A row only to avoid double counting.
            blocks.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{chunk.Block},{chunk.TxCount},{(kind == 0 ? 'A' : 'S')},{total},{(kind == 0 ? chunk.Dropped : 0)},{c[1]},{c[2]},{c[3]},{p[1]},{p[2]},{p[3]},{c[4]},{c[5]},{c[6]},{c[7]},{c[8]},{c[0]},{(rocksTicks[kind] * usPerTick / 1000.0).ToString("F2", CultureInfo.InvariantCulture)}"));
        }
    }
}
