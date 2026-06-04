// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateDiff.Core.Data;
using Nethermind.StateDiff.Core.Diff;
using Nethermind.StateDiffsWriter.Data;
using Nethermind.StateDiffsWriter.Storage;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateDiffsWriter.Service;

/// <summary>
/// Per-block diff producer: subscribes to <see cref="IBlockTree.NewHeadBlock"/>,
/// computes <see cref="TrieDiff"/> between parent and new state root via
/// <see cref="TrieDiffWalker"/>, normalises the slot-count deltas against the
/// running <c>SlotCounts</c> CF, resolves code sizes from the global code DB,
/// then atomically persists the <see cref="BlockDiffRecord"/> through
/// <see cref="BlockDiffsStore"/>.
///
/// <para>
/// Subscription point — the plugin attaches to <see cref="IBlockTree.NewHeadBlock"/>
/// rather than <c>IBranchProcessor.BlockProcessed</c> because the parent state
/// root MUST be readable when the diff runs. NewHeadBlock fires after the trie
/// store has committed the block, which is the only signal that guarantees both
/// (parent_root, new_root) resolve through the FlatDb backing store. The legacy
/// StateComposition plugin uses the same subscription point for the same reason.
/// </para>
///
/// <para>
/// Concurrency — NewHeadBlock fires on a single thread (the block-tree's main
/// loop). The handler runs synchronously per-block; no background task or queue.
/// The <see cref="DiffsPruner"/> runs on its own task and shares
/// <see cref="_writeLock"/> with this service to avoid interleaving writes that
/// touch the BlockDiffs CF.
/// </para>
/// </summary>
public sealed class DiffsWriterService : IDisposable
{
    // Single-writer lock protecting both the per-block write batch and the
    // pruner's bulk delete batch. NewHeadBlock is already single-threaded but
    // the pruner runs on its own task, so the lock is non-decorative.
    internal readonly object WriteLock = new();

    private readonly IBlockTree _blockTree;
    private readonly IWorldStateManager _worldStateManager;
    private readonly IStateReader _stateReader;
    private readonly BlockDiffsStore _store;
    private readonly ILogger _logger;

    private readonly TrieDiffWalker _walker = new();

    private long _lastWrittenBlock = -1;

    public DiffsWriterService(
        IBlockTree blockTree,
        IWorldStateManager worldStateManager,
        IStateReader stateReader,
        BlockDiffsStore store,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        _worldStateManager = worldStateManager;
        _stateReader = stateReader;
        _store = store;
        _logger = logManager.GetClassLogger<DiffsWriterService>();

        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    public long LastWrittenBlock => System.Threading.Volatile.Read(ref _lastWrittenBlock);

    public void Dispose() => _blockTree.NewHeadBlock -= OnNewHeadBlock;

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Block block = e.Block;
        if (block.Header.StateRoot is null) return;

        BlockHeader? parent = _blockTree.FindHeader(block.Number - 1, BlockTreeLookupOptions.RequireCanonical);
        if (parent?.StateRoot is null)
        {
            // Parent header missing / non-canonical: still bump the labeled error
            // counter so operators can alert on sustained gaps (e.g. during a
            // reorg storm) instead of having to grep the log.
            Metrics.StateDiffsWriterEncodeErrorsTotal.AddOrUpdate(
                StateDiffsWriterEncodeErrorReasons.ParentMissing, 1, static (_, v) => v + 1);
            return;
        }

        if (parent.StateRoot == block.Header.StateRoot)
        {
            // No-op block — still publish an empty record so the sidecar's tailer
            // sees a contiguous block sequence and knows the chain advanced.
            WriteRecord(new BlockDiffRecord(block.Number, block.Header.StateRoot, [], []));
            UpdateHeadLag(block.Number);
            return;
        }

        try
        {
            long startTicks = Stopwatch.GetTimestamp();
            BlockDiffRecord record = ComputeRecord(parent, block);
            WriteRecord(record);
            double elapsedSeconds = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
            Metrics.StateDiffsWriterEncodeSeconds.Observe(elapsedSeconds);
            UpdateHeadLag(block.Number);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Metrics.StateDiffsWriterEncodeErrorsTotal.AddOrUpdate(
                StateDiffsWriterEncodeErrorReasons.Compute, 1, static (_, v) => v + 1);
            if (_logger.IsError)
                _logger.Error(
                    $"StateDiffsWriter: failed to compute diff at block {block.Number} " +
                    $"(parentRoot={parent.StateRoot}, newRoot={block.Header.StateRoot})",
                    ex);
        }
    }

    private void UpdateHeadLag(long lastWrittenBlock)
    {
        long head = _blockTree.Head?.Number ?? lastWrittenBlock;
        long lag = head - lastWrittenBlock;
        Metrics.StateDiffsWriterHeadLagBlocks = lag < 0 ? 0 : lag;
    }

    internal BlockDiffRecord ComputeRecord(BlockHeader parent, Block block)
    {
        Hash256 parentRoot = parent.StateRoot!;
        Hash256 newRoot = block.Header.StateRoot!;

        // FlatDb's BeginScope materialises the snapshot bundle for one block,
        // so a diff across two roots needs one scope per side — otherwise the
        // off-side nodes resolve as Unknown and the walker silently emits an
        // empty diff.
        using IReadOnlyTrieStore oldStore = _worldStateManager.CreateReadOnlyTrieStore();
        using IDisposable oldScope = oldStore.BeginScope(parent);
        using IReadOnlyTrieStore newStore = _worldStateManager.CreateReadOnlyTrieStore();
        using IDisposable newScope = newStore.BeginScope(block.Header);

        IScopedTrieStore oldResolver = oldStore.GetTrieStore(null);
        IScopedTrieStore newResolver = newStore.GetTrieStore(null);

        TrieDiff diff = _walker.ComputeDiff(parentRoot, newRoot, oldResolver, newResolver);

        List<CodeHashEntry> codeEntries = new(diff.CodeHashChanges.Count);
        foreach (CodeHashChange change in diff.CodeHashChanges)
        {
            uint newCodeSize = 0;
            if (change.HasCode)
            {
                byte[]? code = _stateReader.GetCode(change.NewCodeHash);
                newCodeSize = (uint)(code?.Length ?? 0);
            }
            codeEntries.Add(new CodeHashEntry(change.OldCodeHash, change.NewCodeHash, newCodeSize));
        }

        List<SlotCountEntry> slotEntries = new(diff.SlotCountChanges.Count);
        foreach (SlotCountChange change in diff.SlotCountChanges)
        {
            ulong oldCount = _store.GetSlotCount(change.AddressHash);
            ulong newCount = ApplyDelta(oldCount, change.SlotDelta);
            slotEntries.Add(new SlotCountEntry(change.AddressHash, oldCount, newCount));
        }

        return new BlockDiffRecord(
            block.Number,
            newRoot,
            codeEntries,
            slotEntries,
            AccountTrieBytesDelta: diff.AccountTrieBytesDelta,
            StorageTrieBytesDelta: diff.StorageTrieBytesDelta,
            AccountsAddedDelta: diff.AccountsAddedDelta);
    }

    internal void WriteRecord(BlockDiffRecord record)
    {
        int payloadBytes;
        try
        {
            lock (WriteLock)
            {
                payloadBytes = _store.WriteBlockDiff(record);
                System.Threading.Volatile.Write(ref _lastWrittenBlock, record.BlockNumber);

                // Flush the Default column family's memtable to disk so the
                // sidecar (RocksDB secondary mode) sees this block via
                // TryCatchUpWithPrimary. Without it the diff sits in the
                // memtable indefinitely and the secondary's iterator can't
                // read it, so the orchestrator's waitSensorForBlock times out.
                _store.FlushDefault();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bumping the labeled counter here (rather than at the call site)
            // covers both NewHeadBlock writes and any external WriteRecord caller.
            Metrics.StateDiffsWriterEncodeErrorsTotal.AddOrUpdate(
                StateDiffsWriterEncodeErrorReasons.Write, 1, static (_, v) => v + 1);
            throw;
        }

        Metrics.StateDiffsWriterLastBlock = record.BlockNumber;
        Metrics.StateDiffsWriterPayloadBytesTotal += payloadBytes;
        Metrics.StateDiffsWriterBlocksWrittenTotal++;
    }

    private static ulong ApplyDelta(ulong oldCount, long delta)
    {
        if (delta >= 0)
        {
            return oldCount + (ulong)delta;
        }

        ulong magnitude = (ulong)(-delta);
        return magnitude >= oldCount ? 0 : oldCount - magnitude;
    }
}
