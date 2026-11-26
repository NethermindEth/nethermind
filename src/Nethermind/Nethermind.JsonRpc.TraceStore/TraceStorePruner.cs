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
    private readonly Lock _checkpointLock = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _worker = Task.CompletedTask;
    private ulong _next;
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

        _next = LoadCheckpoint();
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        _wake.Writer.TryWrite(true);
        _worker = Task.Run(RunAsync);
        if (_logger.IsDebug) _logger.Debug($"TraceStore pruning is enabled, keeping last {blockToKeep} blocks, checking from block {_next}.");
    }

    private ulong LoadCheckpoint()
    {
        if (!_db.WasRepairedOnOpen && _db.Get(CheckpointKey) is { Length: sizeof(ulong) } checkpoint)
            return BinaryPrimitives.ReadUInt64BigEndian(checkpoint);

        // Without a valid checkpoint any block may have traces, so a store that is not empty is checked from genesis.
        using (IEnumerator<byte[]> keys = _db.GetAllKeys().GetEnumerator())
        {
            if (keys.MoveNext()) return 0;
        }

        // Blocks traced from now on are above the window, and a deeper reorganization moves the checkpoint back.
        ulong next = _blockTree.Head is { } head && head.Number >= _blockToKeep ? head.Number - _blockToKeep + 1 : 0;
        WriteCheckpoint(_db, next);
        return next;
    }

    private static void WriteCheckpoint(IWriteOnlyKeyValueStore store, ulong next)
    {
        byte[] value = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, next);
        store.Set(CheckpointKey, value);
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        ulong number = e.Block.Number;
        if (number < Volatile.Read(ref _next))
        {
            // A reorganization deeper than the window traced blocks the checkpoint has already passed.
            lock (_checkpointLock)
            {
                if (number < _next)
                {
                    _next = number;
                    WriteCheckpoint(_db, number);
                }
            }
        }

        _wake.Writer.TryWrite(true);
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync())
            {
                _wake.Reader.TryRead(out _);
                while (!_stopped && PruneNextBatch()) { }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("TraceStore pruning failed; it resumes from the last checkpoint on the next start.", e);
        }
    }

    /// <returns>Whether a batch of levels was processed; <see langword="false"/> once the checkpoint reached the window.</returns>
    private bool PruneNextBatch()
    {
        ulong from = Volatile.Read(ref _next);
        if (_blockTree.Head is not { } head || head.Number < _blockToKeep) return false;
        ulong last = head.Number - _blockToKeep;
        if (last < from) return false;
        if (last - from >= LevelsPerBatch) last = from + LevelsPerBatch - 1;

        int count = (int)(last - from + 1);
        using ArrayPoolListRef<ulong> numbers = new(count);
        for (ulong number = from; number <= last; number++) numbers.Add(number);

        using IOwnedReadOnlyList<ChainLevelInfo?> levels = _levels.MultiLoadLevel(numbers);
        using ArrayPoolListRef<Hash256> expired = new(count);
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] is not { } level) continue;
            foreach (BlockInfo blockInfo in level.BlockInfos)
            {
                if (_db.KeyExists(blockInfo.BlockHash.Bytes)) expired.Add(blockInfo.BlockHash);
            }
        }

        lock (_checkpointLock)
        {
            // A reorganization moved the checkpoint below this batch; check again from there.
            if (_stopped || _next != from) return !_stopped;
            using (IWriteBatch batch = _db.StartWriteBatch())
            {
                foreach (Hash256 hash in expired.AsSpan()) batch.Remove(hash.Bytes);
                WriteCheckpoint(batch, last + 1);
            }

            _next = last + 1;
        }

        if (_logger.IsTrace) _logger.Trace($"Removed {expired.Count} traces from TraceStore on levels {from} to {last}.");
        return true;
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
