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
/// that checkpoint is checked by its keys, so traces kept by an earlier, larger window are removed too.
/// </remarks>
public class TraceStorePruner : IStoppableService, IDisposable
{
    /// <summary>Key of the next unchecked block number; not 32 bytes long, so it never collides with a block hash.</summary>
    internal static readonly byte[] CheckpointKey = "trace-pruning-checkpoint-v1"u8.ToArray();
    private const int BatchSize = 256;
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
    private bool _loaded;
    private bool _failed;
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
        bool running;
        do
        {
            // The pass after the stop only moves the checkpoint back to a block added below it, so the next start does not skip that block.
            running = await _wake.Reader.WaitToReadAsync();
            _wake.Reader.TryRead(out _);
            try
            {
                while (PruneNextBatch()) { }
                _failed = false;
            }
            catch (Exception e)
            {
                // Every block retries, so only the first failure in a row is reported.
                if (!_failed && _logger.IsError) _logger.Error("TraceStore pruning failed; it retries from the last checkpoint on every block.", e);
                _failed = true;
            }
        }
        while (running);
    }

    /// <returns>Whether to continue; <see langword="false"/> once the checkpoint reached the window or pruning stopped.</returns>
    private bool PruneNextBatch()
    {
        ulong changed = Interlocked.Exchange(ref _lowestChanged, ulong.MaxValue);
        if (!_loaded)
        {
            // Checking the keys of the store also covers the blocks added so far.
            if (LoadCheckpoint() is not { } checkpoint) return PruneStore();
            _next = checkpoint;
            _loaded = true;
        }

        if (changed < _next)
        {
            // A block replaced a level the checkpoint has passed, by a reorganization deeper than the window or a rewind.
            try
            {
                WriteCheckpoint(_db, changed);
            }
            catch
            {
                LowerChangedLevel(changed);
                throw;
            }

            _next = changed;
        }

        ulong from = _next;
        if (_stopped || !TryGetCutoff(out ulong last) || last < from) return false;
        if (last - from >= BatchSize) last = from + BatchSize - 1;

        int count = (int)(last - from + 1);
        using ArrayPoolListRef<ulong> numbers = new(count);
        for (ulong number = from; number <= last; number++) numbers.Add(number);

        using IOwnedReadOnlyList<ChainLevelInfo?>? levels = LoadLevels(numbers);
        // A level that cannot be read must not keep the newer ones from expiring, and the keys of the store need no levels.
        if (levels is null) return PruneStore();
        using ArrayPoolListRef<Hash256> expired = new(count);
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] is not { } level) continue;
            foreach (BlockInfo blockInfo in level.BlockInfos)
            {
                if (_db.KeyExists(blockInfo.BlockHash.Bytes)) expired.Add(blockInfo.BlockHash);
            }
        }

        // The levels read above are stale if a block was added at or below them since.
        if (Volatile.Read(ref _lowestChanged) <= last || !TryRemove(expired.AsSpan(), last, last + 1)) return !_stopped;

        _next = last + 1;
        if (_logger.IsTrace) _logger.Trace($"Removed {expired.Count} traces from TraceStore on levels {from} to {last}.");
        return true;
    }

    /// <summary>Checks a store without a valid checkpoint by its keys, so the work depends on its size and not on the chain height.</summary>
    /// <returns>Whether to continue; <see langword="false"/> while there is no block outside the window or once pruning stopped.</returns>
    private bool PruneStore()
    {
        if (_stopped || !TryGetCutoff(out ulong cutoff)) return false;

        using ArrayPoolListRef<Hash256> expired = new(BatchSize);
        foreach (byte[] key in _db.GetAllKeys())
        {
            if (_stopped) return false;
            if (key.Length != Hash256.Size) continue;
            Hash256 hash = new(key);
            // The trace of a block the node does not know stays, as its block number is unknown.
            if (_blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is not { } header || header.Number > cutoff) continue;
            expired.Add(hash);
            if (expired.Count < BatchSize) continue;
            if (!TryRemove(expired.AsSpan(), cutoff, null)) return !_stopped;
            expired.Clear();
        }

        // A block added below the cutoff since the keys were listed moves the checkpoint back in the next batch.
        if (!TryRemove(expired.AsSpan(), cutoff, cutoff + 1)) return !_stopped;
        _next = cutoff + 1;
        _loaded = true;
        return true;
    }

    /// <summary>Removes the traces, and stores the checkpoint with them, unless pruning stopped or the head went back and <paramref name="last"/> is inside the window again.</summary>
    private bool TryRemove(ReadOnlySpan<Hash256> expired, ulong last, ulong? checkpoint)
    {
        if (_stopped || !TryGetCutoff(out ulong cutoff) || cutoff < last) return false;

        using IWriteBatch batch = _db.StartWriteBatch();
        foreach (Hash256 hash in expired) batch.Remove(hash.Bytes);
        if (checkpoint is { } next) WriteCheckpoint(batch, next);
        return true;
    }

    /// <summary>Reads the levels; <see langword="null"/> if they cannot be read.</summary>
    private IOwnedReadOnlyList<ChainLevelInfo?>? LoadLevels(in ArrayPoolListRef<ulong> numbers)
    {
        try
        {
            return _levels.MultiLoadLevel(numbers);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Reading block levels {numbers[0]} to {numbers[numbers.Count - 1]} for TraceStore pruning failed, checking the stored traces by their keys instead. {e.Message}");
            return null;
        }
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

    private ulong? LoadCheckpoint() =>
        !_db.WasRepairedOnOpen && _db.Get(CheckpointKey) is { Length: sizeof(ulong) } checkpoint
            ? BinaryPrimitives.ReadUInt64BigEndian(checkpoint)
            : null;

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
