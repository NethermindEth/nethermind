// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Repositories;

namespace Nethermind.JsonRpc.TraceStore;

/// <summary>
/// Prunes tracing history
/// </summary>
/// <remarks>
/// A background worker removes the traces of every block at or below the head minus the number of blocks to keep,
/// and persists the next unchecked block number in the trace database, atomically with the removals. A store without
/// that checkpoint is checked from genesis, so traces kept by an earlier, larger window are removed too.
/// </remarks>
public class TraceStorePruner : IStoppableService, IDisposable
{
    /// <summary>Key of the next unchecked block number; not 32 bytes long, so it never collides with a block hash.</summary>
    internal static readonly byte[] CheckpointKey = "trace-pruning-checkpoint-v1"u8.ToArray();
    private const int LevelsPerBatch = 256;
    private readonly IBlockTree _blockTree;
    private readonly IChainLevelInfoRepository _levels;
    private readonly IDb _db;
    private readonly ulong _blockToKeep;
    private readonly ILogger _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _worker = Task.CompletedTask;
    private ulong _lowestChanged = ulong.MaxValue;
    private ulong _next;
    private ulong _skipped = ulong.MaxValue;
    private bool _loaded;
    private volatile bool _stopped;

    /// <summary>Starts removing the traces outside <see cref="ITraceStoreConfig.BlocksToKeep"/>, unless it is <c>0</c>.</summary>
    public TraceStorePruner(IBlockTree blockTree, IChainLevelInfoRepository levels, [KeyFilter(TraceStorePlugin.DbName)] IDb db, ITraceStoreConfig traceStoreConfig, ILogManager logManager)
        : this(blockTree, levels, db, traceStoreConfig.BlocksToKeep, logManager)
    {
    }

    [ConstructorWithSideEffect]
    internal TraceStorePruner(IBlockTree blockTree, IChainLevelInfoRepository levels, IDb db, ulong blockToKeep, ILogManager logManager)
    {
        _blockTree = blockTree;
        _levels = levels;
        _db = db;
        _blockToKeep = blockToKeep;
        _logger = logManager.GetClassLogger<TraceStorePruner>();
        if (blockToKeep == 0)
        {
            // Keeping everything does not follow reorganizations, so a later finite window checks the whole store again.
            if (_db.KeyExists(CheckpointKey)) _db.Remove(CheckpointKey);
            return;
        }

        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        _wake.Writer.TryWrite(true);
        _worker = Task.Run(RunAsync);
        if (_logger.IsDebug) _logger.Debug($"TraceStore pruning is enabled, keeping last {blockToKeep} blocks.");
    }

    // Runs on the block processing thread, so it only records the level and signals the worker.
    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        LowerChangedLevel(e.Block.Number);
        _wake.Writer.TryWrite(true);
    }

    private void LowerChangedLevel(ulong number)
    {
        ulong current = Volatile.Read(ref _lowestChanged);
        while (number < current)
        {
            ulong seen = Interlocked.CompareExchange(ref _lowestChanged, number, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private async Task RunAsync()
    {
        while (await _wake.Reader.WaitToReadAsync())
        {
            _wake.Reader.TryRead(out _);
            try
            {
                while (!_stopped && PruneNextBatch()) { }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("TraceStore pruning failed; it retries from the last checkpoint on the next block.", e);
            }
        }
    }

    /// <returns>Whether to continue; <see langword="false"/> once the checkpoint reached the window.</returns>
    private bool PruneNextBatch()
    {
        if (!_loaded)
        {
            _next = LoadCheckpoint();
            _loaded = true;
        }

        ulong changed = Interlocked.Exchange(ref _lowestChanged, ulong.MaxValue);
        if (changed < _next)
        {
            // A block replaced a level the checkpoint has passed, by a reorganization deeper than the window or a rewind.
            try
            {
                WriteCheckpoint(_db, Math.Min(_skipped, changed));
            }
            catch
            {
                LowerChangedLevel(changed);
                throw;
            }

            _next = changed;
            if (changed <= _skipped) _skipped = ulong.MaxValue;
        }

        ulong from = _next;
        if (!TryGetCutoff(out ulong last) || last < from) return false;
        if (last - from >= LevelsPerBatch) last = from + LevelsPerBatch - 1;

        int count = (int)(last - from + 1);
        using ArrayPoolListRef<ulong> numbers = new(count);
        for (ulong number = from; number <= last; number++) numbers.Add(number);

        ulong skipped = _skipped;
        using IOwnedReadOnlyList<ChainLevelInfo?> levels = LoadLevels(numbers, ref skipped);
        using ArrayPoolListRef<Hash256> expired = new(count);
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] is not { } level) continue;
            foreach (BlockInfo blockInfo in level.BlockInfos)
            {
                if (_db.KeyExists(blockInfo.BlockHash.Bytes)) expired.Add(blockInfo.BlockHash);
            }
        }

        // The levels read above are stale if a block was added at or below them since, and the window moved if the head went back.
        if (_stopped || Volatile.Read(ref _lowestChanged) <= last || !TryGetCutoff(out ulong cutoff) || cutoff < last) return !_stopped;

        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            foreach (Hash256 hash in expired.AsSpan()) batch.Remove(hash.Bytes);
            WriteCheckpoint(batch, Math.Min(skipped, last + 1));
        }

        _next = last + 1;
        _skipped = skipped;
        if (_logger.IsTrace) _logger.Trace($"Removed {expired.Count} traces from TraceStore on levels {from} to {last}.");
        return true;
    }

    /// <summary>Reads the levels; a level that cannot be read is <see langword="null"/> and lowers <paramref name="skipped"/> to its number.</summary>
    private IOwnedReadOnlyList<ChainLevelInfo?> LoadLevels(in ArrayPoolListRef<ulong> numbers, ref ulong skipped)
    {
        try
        {
            return _levels.MultiLoadLevel(numbers);
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Reading block levels {numbers[0]} to {numbers[numbers.Count - 1]} for TraceStore pruning failed, reading them one by one. {e.Message}");
        }

        ArrayPoolList<ChainLevelInfo?> levels = new(numbers.Count, numbers.Count);
        for (int i = 0; i < numbers.Count; i++)
        {
            try
            {
                levels[i] = _levels.LoadLevelWithoutCaching(numbers[i]);
            }
            catch (Exception e)
            {
                // Newer levels still expire; the checkpoint stays at this level, so the next start reads it again.
                skipped = Math.Min(skipped, numbers[i]);
                if (_logger.IsWarn) _logger.Warn($"TraceStore pruning skipped unreadable block level {numbers[i]} until the next start. {e.Message}");
            }
        }

        return levels;
    }

    private bool TryGetCutoff(out ulong cutoff)
    {
        if (_blockTree.Head is { } head && head.Number >= _blockToKeep)
        {
            cutoff = head.Number - _blockToKeep;
            return true;
        }

        cutoff = 0;
        return false;
    }

    private ulong LoadCheckpoint()
    {
        if (!_db.WasRepairedOnOpen && _db.Get(CheckpointKey) is { Length: sizeof(ulong) } checkpoint)
            return BinaryPrimitives.ReadUInt64BigEndian(checkpoint);

        // Without a valid checkpoint any block may have traces, so a store that is not empty is checked from genesis.
        if (!IsEmpty()) return 0;

        // Blocks traced from now on are above the window, and a block added below it moves the checkpoint back.
        ulong next = TryGetCutoff(out ulong cutoff) ? cutoff + 1 : 0;
        WriteCheckpoint(_db, next);
        return next;
    }

    private bool IsEmpty()
    {
        if (_db is ISortedKeyValueStore sorted) return sorted.FirstKey is null;
        using IEnumerator<byte[]> keys = _db.GetAllKeys().GetEnumerator();
        return !keys.MoveNext();
    }

    private static void WriteCheckpoint(IWriteOnlyKeyValueStore store, ulong next)
    {
        byte[] value = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, next);
        store.Set(CheckpointKey, value);
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        _stopped = true;
        _wake.Writer.TryComplete();
        return _worker;
    }

    /// <summary>Stops pruning and waits for the batch in progress.</summary>
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
