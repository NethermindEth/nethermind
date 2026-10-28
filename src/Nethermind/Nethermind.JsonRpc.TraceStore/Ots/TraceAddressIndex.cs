// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>Maintains a separately persisted, consecutive index of stored transaction call involvement.</summary>
/// <remarks>Invalid checkpoints disable acceleration until an operator resets this index. Traces remain authoritative.</remarks>
public sealed class TraceAddressIndex : IStoppableService, IAsyncDisposable
{
    internal const string DbName = "TraceAddressIndex";
    internal static readonly byte[] CheckpointKey = "trace-address-checkpoint-v2"u8.ToArray();
    private const int CheckpointSize = 8 + 2 * Hash256.Size;
    private readonly IDb _traces;
    private readonly ITraceSerializer<ParityLikeTxTrace> _serializer;
    private readonly IBlockFinder _blocks;
    private readonly ITraceStoreConfig _config;
    private readonly ILogger _logger;
    private readonly LogIndexStorage _storage;
    private readonly IColumnsDb<LogIndexColumns> _db;
    private readonly IDb _meta;
    private readonly TracePositionSidecar? _positions;
    private readonly Lock _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _worker = Task.CompletedTask;
    private Task? _stopping;
    private Task? _disposing;
    private volatile Checkpoint? _checkpoint;
    private volatile bool _invalid;
    private volatile bool _stopped;
    private bool _started;
    private bool _loaded;
    private string? _lastPause;

    /// <summary>Opens the owned postings database; the startup step starts its background builder.</summary>
    public TraceAddressIndex(IDbFactory dbFactory, [KeyFilter(TraceStorePlugin.DbName)] IDb traces,
        ITraceSerializer<ParityLikeTxTrace> serializer, IBlockFinder blocks, ITraceStoreConfig config, ILogManager logManager)
        : this(dbFactory, traces, serializer, blocks, config, logManager,
            AppContext.TryGetSwitch(TracePositionSidecar.Switch, out bool enabled) && enabled)
    {
    }

    internal TraceAddressIndex(IDbFactory dbFactory, IDb traces, ITraceSerializer<ParityLikeTxTrace> serializer,
        IBlockFinder blocks, ITraceStoreConfig config, ILogManager logManager, bool positionsEnabled)
    {
        if (positionsEnabled && config.AddressIndexReset)
            throw new ArgumentException("The experimental position sidecar requires AddressIndexReset=false.", nameof(config));
        if (config.BlocksToKeep != 0 || (config.TraceTypes & ParityTraceTypes.Trace) == 0)
            throw new ArgumentException("TraceStore.AddressIndexEnabled requires BlocksToKeep=0 and TraceTypes including Trace.", nameof(config));
        if (config.AddressIndexFromBlock < 1 || config.AddressIndexLag < 0)
            throw new ArgumentException("Trace address index start must be positive and lag must be nonnegative.", nameof(config));
        _traces = traces;
        _serializer = serializer;
        _blocks = blocks;
        _config = config;
        _logger = logManager.GetClassLogger<TraceAddressIndex>();
        IndexDbFactory factory = new(dbFactory);
        _storage = new LogIndexStorage(factory, logManager, new LogIndexConfig
        {
            Enabled = true,
            Reset = config.AddressIndexReset,
            MaxReorgDepth = 0,
            MaxCompressionParallelism = 1
        });
        _db = factory.Database;
        _meta = _db.GetColumnDb(LogIndexColumns.Meta);
        if (positionsEnabled) _positions = new(_db, traces, serializer, blocks, config, _logger);
    }

    private void LoadCheckpoint()
    {
        using Lock.Scope _ = _lifecycle.EnterScope();
        if (_loaded) return;
        byte[]? checkpoint = _meta.Get(CheckpointKey);
        bool repaired = _db.WasRepairedOnOpen;
        _loaded = true;
        if (repaired)
            Invalidate("database was repaired");
        else if (checkpoint is null && _storage.MinBlockNumber is null && _storage.MaxBlockNumber is null)
            return;
        else if (checkpoint is not { Length: CheckpointSize })
            Invalidate("missing or malformed checkpoint");
        else
        {
            int from = BinaryPrimitives.ReadInt32LittleEndian(checkpoint);
            int to = BinaryPrimitives.ReadInt32LittleEndian(checkpoint.AsSpan(4));
            if (from != _config.AddressIndexFromBlock || from > to || from != _storage.MinBlockNumber || to != _storage.MaxBlockNumber)
                Invalidate("checkpoint does not match postings coverage or configured start");
            else
                _checkpoint = new(from, to, new Hash256(checkpoint.AsSpan(8, Hash256.Size)), new Hash256(checkpoint.AsSpan(8 + Hash256.Size)));
        }
    }

    internal sealed record Checkpoint(int From, int To, Hash256 Genesis, Hash256 Hash);
    internal sealed record Candidates(Checkpoint Coverage, int[] Blocks, Hash256[] Hashes, Hash256 FirstParent);

    internal void Start()
    {
        using Lock.Scope _ = _lifecycle.EnterScope();
        if (_started || _stopped) return;
        _started = true;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            bool bulkBuild = _positions is not null
                && AppContext.TryGetSwitch("Nethermind.Experimental.OtsPositionSidecarBulkBuild", out bool enabled) && enabled;
            while (!_stop.IsCancellationRequested)
            {
                // Yield between bounded groups; no aggregation queue or whole-range trace materialization.
                bool worked = false;
                for (int i = 0; i < 64; i++)
                {
                    bool built = await BuildNextAsync(_stop.Token);
                    built |= BuildPositionsNext(_stop.Token);
                    if (!built) break;
                    worked = true;
                }
                if (bulkBuild && worked)
                    await Task.Yield();
                else
                    await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Invalidate("background builder failed");
            if (_logger.IsError) _logger.Error("Trace address index builder failed; OTS will scan stored traces.", error);
        }
    }

    /// <summary>Certifies and persists at most one additional canonical block.</summary>
    /// <remarks>Single writer: invoked by the worker, or directly by tests before starting it.</remarks>
    internal async Task<bool> BuildNextAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_stopped || _invalid || _blocks.Head is not { } head) return false;
        LoadCheckpoint();
        if (_invalid) return false;
        Checkpoint? previous = _checkpoint;
        if (previous is not null && !IsCanonical(previous))
        {
            Invalidate("canonical checkpoint changed");
            return false;
        }
        long next = previous is null ? _config.AddressIndexFromBlock : (long)previous.To + 1;
        if (next > int.MaxValue || head.Number < (ulong)_config.AddressIndexLag || (ulong)next > head.Number - (ulong)_config.AddressIndexLag)
            return false;
        Block? block = _blocks.FindBlock((ulong)next);
        if (block is null) return Pause($"Block {next} unavailable");
        if (previous is not null && block.ParentHash != previous.Hash)
        {
            Invalidate("nonconsecutive canonical blocks");
            return false;
        }
        LogIndexAggregate aggregate = new((int)next, (int)next, false);
        if (block.Transactions.Length != 0)
        {
            SearchResult<List<ParityLikeTxTrace>> read = OtsTraceReader.Read(_traces, _serializer, _config, block.Header);
            if (read.IsError) return Pause(read.Error!);
            List<ParityLikeTxTrace> traces = read.Object!;
            if (OtsTraceReader.Validate(block, traces, ct) is { } error) return Pause(error);
            foreach (ParityLikeTxTrace trace in traces)
            {
                if (trace.TransactionHash is null) continue;
                Stack<ParityTraceAction> stack = new();
                stack.Push(trace.Action!);
                while (stack.TryPop(out ParityTraceAction? action))
                {
                    ct.ThrowIfCancellationRequested();
                    Add(action.From);
                    Add(action.To);
                    Add(action.Result?.Address);
                    foreach (ParityTraceAction child in action.Subtraces) stack.Push(child);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        Checkpoint checkpoint = new(_config.AddressIndexFromBlock, (int)next, _blocks.GenesisHash, block.Hash!);
        if (!IsCanonical(checkpoint)) return Pause("Canonical chain changed before index write");
        try
        {
            // Identities precede postings in the same WAL. An uncheckpointed identity is ignored on restart;
            // postings without their following checkpoint leave a range mismatch and disable acceleration.
            byte[] identity = new byte[2 * Hash256.Size];
            block.Hash!.Bytes.CopyTo(identity);
            block.ParentHash!.Bytes.CopyTo(identity.AsSpan(Hash256.Size));
            _meta.Set(BlockKey((int)next), identity);
            await _storage.AddReceiptsAsync(aggregate);
            _db.SyncWal();
            if (!IsCanonical(checkpoint))
            {
                Invalidate("canonical chain changed during index write");
                return false;
            }
            byte[] value = new byte[CheckpointSize];
            BinaryPrimitives.WriteInt32LittleEndian(value, checkpoint.From);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), checkpoint.To);
            checkpoint.Genesis.Bytes.CopyTo(value.AsSpan(8));
            checkpoint.Hash.Bytes.CopyTo(value.AsSpan(8 + Hash256.Size));
            _meta.Set(CheckpointKey, value);
            _db.SyncWal();
            _checkpoint = checkpoint;
            _lastPause = null;
            if (_logger.IsInfo && (previous is null || next % 1024 == 0 || (ulong)next == head.Number - (ulong)_config.AddressIndexLag))
                _logger.Info($"Trace address index coverage: {checkpoint.From}-{checkpoint.To} ({checkpoint.Hash}).");
            return true;
        }
        catch
        {
            Invalidate("index write failed; checkpoint may be incomplete");
            throw;
        }

        void Add(Address? address)
        {
            if (address is not null && !aggregate.Address.ContainsKey(address)) aggregate.Address.Add(address, [(int)next]);
        }
    }

    internal bool BuildPositionsNext(CancellationToken ct)
    {
        if (_positions is null || _stopped || _invalid) return false;
        LoadCheckpoint();
        return !_invalid && _checkpoint is { } coverage && IsCanonical(coverage) && _positions.BuildNext(coverage, ct);
    }

    internal SearchResult<bool[]>? TryMatchPositions(Block block, Address address, CancellationToken ct)
    {
        if (_positions is null) return null;
        while (!_lifecycle.TryEnter(TimeSpan.FromMilliseconds(50))) ct.ThrowIfCancellationRequested();
        try
        {
            ct.ThrowIfCancellationRequested();
            return _stopped || _invalid ? null : _positions.TryMatch(block, address, ct);
        }
        finally { _lifecycle.Exit(); }
    }

    internal Candidates? FindCandidates(Address address, ulong from, ulong to, bool backwards, int limit, CancellationToken ct)
    {
        while (!_lifecycle.TryEnter(TimeSpan.FromMilliseconds(50))) ct.ThrowIfCancellationRequested();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_stopped || _invalid || _storage.HasBackgroundError) return null;
            LoadCheckpoint();
            if (_invalid || _checkpoint is not { } checkpoint) return null;
            if (!IsCanonical(checkpoint))
            {
                Invalidate("canonical checkpoint changed");
                return null;
            }
            int lower = (int)Math.Min(int.MaxValue, Math.Max(from, (ulong)checkpoint.From));
            int upper = (int)Math.Min(to, (ulong)checkpoint.To);
            if (from > (ulong)checkpoint.To || to < (ulong)checkpoint.From) return null;
            Queue<int> candidates = new(limit);
            IEnumerator<int>? reverse = backwards ? _storage.TryGetReverseEnumerator(address, lower, upper) : null;
            if (reverse is not null)
            {
                using (reverse)
                {
                    while (candidates.Count < limit && reverse.MoveNext())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_stopped) return null;
                        candidates.Enqueue(reverse.Current);
                    }
                }
            }
            else
            {
                using IEnumerator<int> numbers = _storage.GetEnumerator(address, lower, upper);
                while (numbers.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    if (_stopped) return null;
                    if (candidates.Count == limit)
                    {
                        if (!backwards) break;
                        candidates.Dequeue();
                    }
                    candidates.Enqueue(numbers.Current);
                }
            }
            int[] result = candidates.ToArray();
            if (backwards && reverse is null) Array.Reverse(result);
            byte[]? first = ReadIdentity(checkpoint.From);
            if (first is null) return null;
            Hash256[] hashes = new Hash256[result.Length];
            for (int i = 0; i < result.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte[]? identity = ReadIdentity(result[i]);
                if (identity is null) return null;
                hashes[i] = new Hash256(identity.AsSpan(0, Hash256.Size));
            }
            return new(checkpoint, result, hashes, new Hash256(first.AsSpan(Hash256.Size)));
        }
        finally { _lifecycle.Exit(); }
    }

    private static byte[] BlockKey(int number)
    {
        byte[] key = new byte[5];
        key[0] = (byte)'b';
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1), number);
        return key;
    }

    private byte[]? ReadIdentity(int number)
    {
        byte[]? identity = _meta.Get(BlockKey(number));
        if (identity is { Length: 2 * Hash256.Size }) return identity;
        Invalidate($"missing or malformed block identity at {number}");
        return null;
    }

    internal bool IsCanonical(Checkpoint checkpoint) => !_invalid && !_storage.HasBackgroundError
        && _blocks.GenesisHash == checkpoint.Genesis
        && _blocks.FindBlockHash((ulong)checkpoint.To) == checkpoint.Hash;

    private bool Pause(string reason)
    {
        if (_lastPause != reason && _logger.IsWarn) _logger.Warn($"Trace address index paused: {reason}; OTS scans outside certified coverage.");
        _lastPause = reason;
        return false;
    }

    private void Invalidate(string reason)
    {
        _invalid = true;
        if (_logger.IsWarn) _logger.Warn($"Trace address index disabled: {reason}. OTS will scan. Restart with TraceStore.AddressIndexReset=true to rebuild this index.");
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _stopped = true;
        using Lock.Scope _ = _lifecycle.EnterScope();
        if (_stopping is not null) return _stopping;
        _stop.Cancel();
        return _stopping = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        await _worker;
        await _storage.StopAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stopped = true;
        using Lock.Scope _ = _lifecycle.EnterScope();
        return new(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAsync(); }
        finally
        {
            try { await ((IAsyncDisposable)_storage).DisposeAsync(); }
            finally { _stop.Dispose(); }
        }
    }

    private sealed class IndexDbFactory(IDbFactory inner) : IDbFactory
    {
        internal IColumnsDb<LogIndexColumns> Database { get; private set; } = null!;
        public IDb CreateDb(DbSettings settings) => throw new NotSupportedException();
        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings settings) where T : struct, Enum
        {
            DbSettings isolated = settings.Clone(DbName, "traceAddressIndex");
            isolated.PersistRepairMarkerUntilAcknowledged = true;
            IColumnsDb<T> db = inner.CreateColumnsDb<T>(isolated);
            Database = (IColumnsDb<LogIndexColumns>)(object)db;
            return db;
        }
    }
}
