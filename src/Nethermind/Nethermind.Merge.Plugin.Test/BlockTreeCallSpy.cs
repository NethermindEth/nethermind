// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Thin <see cref="IBlockTree"/> decorator that counts <see cref="IBlockFinder.FindHeader"/> calls on a real tree.
/// Unlike <see cref="RecordingCommitBlockTree"/>, which is a standalone fake for commit-block unit tests,
/// this wraps production block-tree behavior and only instruments the ancestry walk under test.
/// </summary>
public sealed class BlockTreeCallSpy(IBlockTree inner) : IBlockTree
{
    public int FindHeaderCalls { get; private set; }

    public void ResetCounters() => FindHeaderCalls = 0;

    public static (IBlockTree Proxy, BlockTreeCallSpy Spy) Wrap(IBlockTree inner)
    {
        BlockTreeCallSpy spy = new(inner);
        return (spy, spy);
    }

    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
    {
        FindHeaderCalls++;
        return inner.FindHeader(blockHash, options, blockNumber);
    }

    public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options)
    {
        FindHeaderCalls++;
        return inner.FindHeader(blockNumber, options);
    }

    public ulong NetworkId => inner.NetworkId;
    public ulong ChainId => inner.ChainId;
    public BlockHeader? Genesis => inner.Genesis;
    public BlockHeader? BestSuggestedHeader => inner.BestSuggestedHeader;
    public Block? BestSuggestedBody => inner.BestSuggestedBody;
    public BlockHeader? BestSuggestedBeaconHeader => inner.BestSuggestedBeaconHeader;
    public BlockHeader? LowestInsertedHeader
    {
        get => inner.LowestInsertedHeader;
        set => inner.LowestInsertedHeader = value;
    }
    public BlockHeader? LowestInsertedBeaconHeader
    {
        get => inner.LowestInsertedBeaconHeader;
        set => inner.LowestInsertedBeaconHeader = value;
    }
    public long BestKnownNumber => inner.BestKnownNumber;
    public long BestKnownBeaconNumber => inner.BestKnownBeaconNumber;
    public bool CanAcceptNewBlocks => inner.CanAcceptNewBlocks;
    public (long BlockNumber, Hash256 BlockHash) SyncPivot
    {
        get => inner.SyncPivot;
        set => inner.SyncPivot = value;
    }
    public bool IsProcessingBlock
    {
        get => inner.IsProcessingBlock;
        set => inner.IsProcessingBlock = value;
    }

    public Hash256 HeadHash => inner.HeadHash;
    public Hash256 GenesisHash => inner.GenesisHash;
    public Hash256? PendingHash => inner.PendingHash;
    public Hash256? FinalizedHash => inner.FinalizedHash;
    public Hash256? SafeHash => inner.SafeHash;
    public Block? Head => inner.Head;
    public long? BestPersistedState
    {
        get => inner.BestPersistedState;
        set => inner.BestPersistedState = value;
    }

    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        inner.FindBlock(blockHash, options, blockNumber);
    public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) => inner.FindBlock(blockNumber, options);
    public bool HasBlock(long blockNumber, Hash256 blockHash) => inner.HasBlock(blockNumber, blockHash);
    public Hash256? FindBlockHash(long blockNumber) => inner.FindBlockHash(blockNumber);
    public bool IsMainChain(BlockHeader blockHeader) => inner.IsMainChain(blockHeader);
    public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => inner.IsMainChain(blockHash, throwOnMissingHash);
    public long GetLowestBlock() => inner.GetLowestBlock();
    public BlockHeader FindBestSuggestedHeader() => inner.FindBestSuggestedHeader();

    public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        inner.Insert(header, headerOptions);
    public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        inner.BulkInsertHeader(headers, headerOptions);
    public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) =>
        inner.Insert(block, insertBlockOptions, insertHeaderOptions, bodiesWriteFlags);
    public void UpdateHeadBlock(Hash256 blockHash) => inner.UpdateHeadBlock(blockHash);
    public void NewOldestBlock(long oldestBlock) => inner.NewOldestBlock(oldestBlock);
    public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        inner.SuggestBlock(block, options);
    public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        inner.SuggestBlockAsync(block, options);
    public AddBlockResult SuggestHeader(BlockHeader header) => inner.SuggestHeader(header);
    public bool IsKnownBlock(long number, Hash256 blockHash) => inner.IsKnownBlock(number, blockHash);
    public bool IsKnownBeaconBlock(long number, Hash256 blockHash) => inner.IsKnownBeaconBlock(number, blockHash);
    public bool WasProcessed(long number, Hash256 blockHash) => inner.WasProcessed(number, blockHash);
    public bool TryUpdateMainChain(BlockHeader newHead, bool wereProcessed, bool forceUpdateHeadBlock = false, params ReadOnlySpan<Block> preloadedBlocks) =>
        inner.TryUpdateMainChain(newHead, wereProcessed, forceUpdateHeadBlock, preloadedBlocks);
    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) => inner.MarkChainAsProcessed(blocks);
    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) =>
        inner.Accept(blockTreeVisitor, cancellationToken);
    public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash) => inner.GetInfo(number, blockHash);
    public ChainLevelInfo? FindLevel(long number) => inner.FindLevel(number);
    public BlockInfo FindCanonicalBlockInfo(long blockNumber) => inner.FindCanonicalBlockInfo(blockNumber);
    public Hash256? FindHash(long blockNumber) => inner.FindHash(blockNumber);
    public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) =>
        inner.FindHeaders(hash, numberOfBlocks, skip, reverse);
    public void DeleteInvalidBlock(Block invalidBlock) => inner.DeleteInvalidBlock(invalidBlock);
    public void ReportBadBlock(Block badBlock) => inner.ReportBadBlock(badBlock);
    public void DeleteOldBlock(long blockNumber, Hash256 blockHash) => inner.DeleteOldBlock(blockNumber, blockHash);
    public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) =>
        inner.ForkChoiceUpdated(finalizedBlockHash, safeBlockBlockHash);
    public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) =>
        inner.DeleteChainSlice(startNumber, endNumber, force);
    public bool IsBetterThanHead(BlockHeader? header) => inner.IsBetterThanHead(header);
    public void UpdateBeaconMainChain(IReadOnlyList<BlockInfo>? blockInfos, long clearBeaconMainChainStartPoint) =>
        inner.UpdateBeaconMainChain(blockInfos, clearBeaconMainChainStartPoint);
    public void RecalculateTreeLevels() => inner.RecalculateTreeLevels();

    public event EventHandler<BlockEventArgs> NewBestSuggestedBlock
    {
        add => inner.NewBestSuggestedBlock += value;
        remove => inner.NewBestSuggestedBlock -= value;
    }
    public event EventHandler<BlockEventArgs> NewSuggestedBlock
    {
        add => inner.NewSuggestedBlock += value;
        remove => inner.NewSuggestedBlock -= value;
    }
    public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain
    {
        add => inner.BlockAddedToMain += value;
        remove => inner.BlockAddedToMain -= value;
    }
    public event EventHandler<BlockEventArgs> NewHeadBlock
    {
        add => inner.NewHeadBlock += value;
        remove => inner.NewHeadBlock -= value;
    }
    public event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain
    {
        add => inner.OnUpdateMainChain += value;
        remove => inner.OnUpdateMainChain -= value;
    }
    public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs> OnForkChoiceUpdated
    {
        add => inner.OnForkChoiceUpdated += value;
        remove => inner.OnForkChoiceUpdated -= value;
    }
}
