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
/// Per-block diff producer: on each new head, walks the parent→new state-root diff and atomically
/// persists a <see cref="BlockDiffRecord"/> through <see cref="BlockDiffsStore"/>.
/// </summary>
/// <remarks>
/// NewHeadBlock is the subscription point because it fires only after the trie store commits, the one
/// signal that guarantees both roots resolve through FlatDb.
/// </remarks>
public sealed class DiffsWriterService : IDisposable
{
    internal readonly object WriteLock = new();

    private readonly IBlockTree _blockTree;
    private readonly IWorldStateManager _worldStateManager;
    private readonly IStateReader _stateReader;
    private readonly BlockDiffsStore _store;
    private readonly ILogger _logger;

    private long _lastWrittenBlock = -1;
    private Hash256? _lastWrittenBlockHash;
    private long _reorgsObserved;
    private bool _disposed;

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

    internal long ReorgsObserved => _reorgsObserved;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Block block = e.Block;
        if (block.Header.StateRoot is null) return;

        DetectReorg(block);

        BlockHeader? parent = _blockTree.FindHeader(block.Number - 1, BlockTreeLookupOptions.RequireCanonical);
        if (parent?.StateRoot is null)
        {
            // Bump the labeled counter so operators can alert on sustained parent gaps without grepping logs.
            Metrics.StateDiffsWriterEncodeErrorsTotal.AddOrUpdate(
                StateDiffsWriterEncodeErrorReasons.ParentMissing, 1, static (_, v) => v + 1);
            return;
        }

        if (parent.StateRoot == block.Header.StateRoot)
        {
            // Publish an empty record so an external reader still sees a contiguous block sequence.
            WriteRecord(new BlockDiffRecord((long)block.Number, block.Header.StateRoot, [], []));
            MarkWritten(block);
            return;
        }

        try
        {
            long startTicks = Stopwatch.GetTimestamp();
            BlockDiffRecord record = ComputeRecord(parent, block);
            WriteRecord(record);
            double elapsedSeconds = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
            Metrics.StateDiffsWriterEncodeSeconds.Observe(elapsedSeconds);
            MarkWritten(block);
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

    // SlotCounts is not rolled back on reorg, so OldCount is advisory across one; surfaced via metric + warn.
    private void DetectReorg(Block block)
    {
        Hash256? lastHash = _lastWrittenBlockHash;
        if (lastHash is null || block.Header.ParentHash == lastHash) return;

        _reorgsObserved++;
        Metrics.StateDiffsWriterReorgsTotal = _reorgsObserved;

        if (_logger.IsWarn)
            _logger.Warn(
                $"StateDiffsWriter: new head {block.Number} ({block.Hash}) does not build on " +
                $"last-written block {LastWrittenBlock} ({lastHash}); SlotCounts OldCount is advisory " +
                "across this reorg until the consumer reconstructs running totals from the diff chain.");
    }

    private void MarkWritten(Block block)
    {
        _lastWrittenBlockHash = block.Hash;
        UpdateHeadLag((long)block.Number);
    }

    private void UpdateHeadLag(long lastWrittenBlock)
    {
        long head = _blockTree.Head is { Number: var headNumber } ? (long)headNumber : lastWrittenBlock;
        long lag = head - lastWrittenBlock;
        Metrics.StateDiffsWriterHeadLagBlocks = lag < 0 ? 0 : lag;
    }

    internal BlockDiffRecord ComputeRecord(BlockHeader parent, Block block)
    {
        Hash256 parentRoot = parent.StateRoot!;
        Hash256 newRoot = block.Header.StateRoot!;

        // One scope per root: FlatDb scopes nodes per block, so a single scope leaves the off-side subtree Unknown.
        using IReadOnlyTrieStore oldStore = _worldStateManager.CreateReadOnlyTrieStore();
        using IDisposable oldScope = oldStore.BeginScope(parent);
        using IReadOnlyTrieStore newStore = _worldStateManager.CreateReadOnlyTrieStore();
        using IDisposable newScope = newStore.BeginScope(block.Header);

        IScopedTrieStore oldResolver = oldStore.GetTrieStore(null);
        IScopedTrieStore newResolver = newStore.GetTrieStore(null);

        // Fresh walker per call drops the implicit single-thread invariant of a shared instance.
        TrieDiff diff = new TrieDiffWalker().ComputeDiff(parentRoot, newRoot, oldResolver, newResolver);

        List<CodeHashEntry> codeEntries = new(diff.CodeHashChanges.Count);
        foreach (CodeHashChange change in diff.CodeHashChanges)
        {
            uint newCodeSize = ResolveNewCodeSize(change, (long)block.Number);
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
            (long)block.Number,
            newRoot,
            codeEntries,
            slotEntries,
            AccountTrieBytesDelta: diff.AccountTrieBytesDelta,
            StorageTrieBytesDelta: diff.StorageTrieBytesDelta,
            AccountsAddedDelta: diff.AccountsAddedDelta);
    }

    internal uint ResolveNewCodeSize(in CodeHashChange change, long blockNumber)
    {
        if (!change.HasCode) return 0;

        byte[]? code = _stateReader.GetCode(change.NewCodeHash);
        if (code is null)
        {
            Metrics.StateDiffsWriterEncodeErrorsTotal.AddOrUpdate(
                StateDiffsWriterEncodeErrorReasons.CodeMissing, 1, static (_, v) => v + 1);
            if (_logger.IsWarn)
                _logger.Warn(
                    $"StateDiffsWriter: code not found for hash {change.NewCodeHash} at block {blockNumber}; " +
                    "recording NewCodeSize=0 (a downstream code-bytes total will undercount this entry).");
            return 0;
        }

        return (uint)code.Length;
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
                _store.FlushDefault(); // make the block visible to an external secondary-mode reader
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bumped here, not at the call site, to cover every WriteRecord caller.
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
