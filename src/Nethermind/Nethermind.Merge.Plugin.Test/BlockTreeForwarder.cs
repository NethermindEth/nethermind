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
/// Forwards every <see cref="IBlockTree"/> member to an inner instance. Subclasses override only the calls they
/// need to instrument.
/// </summary>
internal abstract class BlockTreeForwarder(IBlockTree inner) : IBlockTree
{
    protected IBlockTree Inner { get; } = inner;

    public virtual ulong NetworkId => Inner.NetworkId;
    public virtual ulong ChainId => Inner.ChainId;
    public virtual BlockHeader? Genesis => Inner.Genesis;
    public virtual BlockHeader? BestSuggestedHeader => Inner.BestSuggestedHeader;
    public virtual Block? BestSuggestedBody => Inner.BestSuggestedBody;
    public virtual BlockHeader? BestSuggestedBeaconHeader => Inner.BestSuggestedBeaconHeader;
    public virtual BlockHeader? LowestInsertedHeader
    {
        get => Inner.LowestInsertedHeader;
        set => Inner.LowestInsertedHeader = value;
    }
    public virtual BlockHeader? LowestInsertedBeaconHeader
    {
        get => Inner.LowestInsertedBeaconHeader;
        set => Inner.LowestInsertedBeaconHeader = value;
    }
    public virtual long BestKnownNumber => Inner.BestKnownNumber;
    public virtual long BestKnownBeaconNumber => Inner.BestKnownBeaconNumber;
    public virtual bool CanAcceptNewBlocks => Inner.CanAcceptNewBlocks;
    public virtual (long BlockNumber, Hash256 BlockHash) SyncPivot
    {
        get => Inner.SyncPivot;
        set => Inner.SyncPivot = value;
    }
    public virtual bool IsProcessingBlock
    {
        get => Inner.IsProcessingBlock;
        set => Inner.IsProcessingBlock = value;
    }

    public virtual Hash256 HeadHash => Inner.HeadHash;
    public virtual Hash256 GenesisHash => Inner.GenesisHash;
    public virtual Hash256? PendingHash => Inner.PendingHash;
    public virtual Hash256? FinalizedHash => Inner.FinalizedHash;
    public virtual Hash256? SafeHash => Inner.SafeHash;
    public virtual Block? Head => Inner.Head;
    public virtual long? BestPersistedState
    {
        get => Inner.BestPersistedState;
        set => Inner.BestPersistedState = value;
    }

    public virtual Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        Inner.FindBlock(blockHash, options, blockNumber);
    public virtual Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) => Inner.FindBlock(blockNumber, options);
    public virtual bool HasBlock(long blockNumber, Hash256 blockHash) => Inner.HasBlock(blockNumber, blockHash);
    public virtual BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        Inner.FindHeader(blockHash, options, blockNumber);
    public virtual BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options) =>
        Inner.FindHeader(blockNumber, options);
    public virtual Hash256? FindBlockHash(long blockNumber) => Inner.FindBlockHash(blockNumber);
    public virtual bool IsMainChain(BlockHeader blockHeader) => Inner.IsMainChain(blockHeader);
    public virtual bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => Inner.IsMainChain(blockHash, throwOnMissingHash);
    public virtual long GetLowestBlock() => Inner.GetLowestBlock();
    public virtual BlockHeader FindBestSuggestedHeader() => Inner.FindBestSuggestedHeader();

    public virtual AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        Inner.Insert(header, headerOptions);
    public virtual void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        Inner.BulkInsertHeader(headers, headerOptions);
    public virtual AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) =>
        Inner.Insert(block, insertBlockOptions, insertHeaderOptions, bodiesWriteFlags);
    public virtual void UpdateHeadBlock(Hash256 blockHash) => Inner.UpdateHeadBlock(blockHash);
    public virtual void NewOldestBlock(long oldestBlock) => Inner.NewOldestBlock(oldestBlock);
    public virtual AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        Inner.SuggestBlock(block, options);
    public virtual ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        Inner.SuggestBlockAsync(block, options);
    public virtual AddBlockResult SuggestHeader(BlockHeader header) => Inner.SuggestHeader(header);
    public virtual bool IsKnownBlock(long number, Hash256 blockHash) => Inner.IsKnownBlock(number, blockHash);
    public virtual bool IsKnownBeaconBlock(long number, Hash256 blockHash) => Inner.IsKnownBeaconBlock(number, blockHash);
    public virtual bool WasProcessed(long number, Hash256 blockHash) => Inner.WasProcessed(number, blockHash);
    public virtual bool TryUpdateMainChain(BlockHeader newHead, bool wereProcessed, bool forceUpdateHeadBlock = false, params ReadOnlySpan<Block> preloadedBlocks) =>
        Inner.TryUpdateMainChain(newHead, wereProcessed, forceUpdateHeadBlock, preloadedBlocks);
    public virtual void MarkChainAsProcessed(IReadOnlyList<Block> blocks) => Inner.MarkChainAsProcessed(blocks);
    public virtual Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) =>
        Inner.Accept(blockTreeVisitor, cancellationToken);
    public virtual (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash) => Inner.GetInfo(number, blockHash);
    public virtual ChainLevelInfo? FindLevel(long number) => Inner.FindLevel(number);
    public virtual BlockInfo FindCanonicalBlockInfo(long blockNumber) => Inner.FindCanonicalBlockInfo(blockNumber);
    public virtual Hash256? FindHash(long blockNumber) => Inner.FindHash(blockNumber);
    public virtual IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) =>
        Inner.FindHeaders(hash, numberOfBlocks, skip, reverse);
    public virtual void DeleteInvalidBlock(Block invalidBlock) => Inner.DeleteInvalidBlock(invalidBlock);
    public virtual void ReportBadBlock(Block badBlock) => Inner.ReportBadBlock(badBlock);
    public virtual void DeleteOldBlock(long blockNumber, Hash256 blockHash) => Inner.DeleteOldBlock(blockNumber, blockHash);
    public virtual void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) =>
        Inner.ForkChoiceUpdated(finalizedBlockHash, safeBlockBlockHash);
    public virtual int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) =>
        Inner.DeleteChainSlice(startNumber, endNumber, force);
    public virtual bool IsBetterThanHead(BlockHeader? header) => Inner.IsBetterThanHead(header);
    public virtual void UpdateBeaconMainChain(IReadOnlyList<BlockInfo>? blockInfos, long clearBeaconMainChainStartPoint) =>
        Inner.UpdateBeaconMainChain(blockInfos, clearBeaconMainChainStartPoint);
    public virtual void RecalculateTreeLevels() => Inner.RecalculateTreeLevels();

    public event EventHandler<BlockEventArgs> NewBestSuggestedBlock
    {
        add => Inner.NewBestSuggestedBlock += value;
        remove => Inner.NewBestSuggestedBlock -= value;
    }
    public event EventHandler<BlockEventArgs> NewSuggestedBlock
    {
        add => Inner.NewSuggestedBlock += value;
        remove => Inner.NewSuggestedBlock -= value;
    }
    public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain
    {
        add => Inner.BlockAddedToMain += value;
        remove => Inner.BlockAddedToMain -= value;
    }
    public event EventHandler<BlockEventArgs> NewHeadBlock
    {
        add => Inner.NewHeadBlock += value;
        remove => Inner.NewHeadBlock -= value;
    }
    public event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain
    {
        add => Inner.OnUpdateMainChain += value;
        remove => Inner.OnUpdateMainChain -= value;
    }
    public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs> OnForkChoiceUpdated
    {
        add => Inner.OnForkChoiceUpdated += value;
        remove => Inner.OnForkChoiceUpdated -= value;
    }
}
