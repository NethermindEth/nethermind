// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Test.Builders;

/// <summary>
/// <see cref="IBlockTree"/> test double that either stubs unimplemented members (no <paramref name="inner"/>)
/// or forwards to a wrapped tree. Subclasses override only the calls they need to record or instrument.
/// </summary>
public class BlockTreeTestDouble : IBlockTree
{
    private Block? _head;
    private BlockHeader? _bestSuggestedHeader;
    private BlockHeader? _lowestInsertedHeader;
    private BlockHeader? _lowestInsertedBeaconHeader;
    private (ulong BlockNumber, Hash256 BlockHash) _syncPivot;
    private bool _isProcessingBlock;

    protected IBlockTree? Inner { get; }

    protected BlockTreeTestDouble(IBlockTree? inner = null) => Inner = inner;

    public virtual Block? Head
    {
        get => Inner?.Head ?? _head;
        set => _head = value;
    }

    public virtual BlockHeader? BestSuggestedHeader
    {
        get => Inner?.BestSuggestedHeader ?? _bestSuggestedHeader;
        set => _bestSuggestedHeader = value;
    }

    private event EventHandler<BlockReplacementEventArgs>? _blockAddedToMain;

    public event EventHandler<BlockReplacementEventArgs>? BlockAddedToMain
    {
        add
        {
            if (Inner is not null) Inner.BlockAddedToMain += value;
            else _blockAddedToMain += value;
        }
        remove
        {
            if (Inner is not null) Inner.BlockAddedToMain -= value;
            else _blockAddedToMain -= value;
        }
    }

    public event EventHandler<BlockEventArgs>? NewBestSuggestedBlock
    {
        add { if (Inner is not null) Inner.NewBestSuggestedBlock += value; }
        remove { if (Inner is not null) Inner.NewBestSuggestedBlock -= value; }
    }
    public event EventHandler<BlockEventArgs>? NewSuggestedBlock
    {
        add { if (Inner is not null) Inner.NewSuggestedBlock += value; }
        remove { if (Inner is not null) Inner.NewSuggestedBlock -= value; }
    }
    public event EventHandler<BlockEventArgs>? NewHeadBlock
    {
        add { if (Inner is not null) Inner.NewHeadBlock += value; }
        remove { if (Inner is not null) Inner.NewHeadBlock -= value; }
    }
    public event EventHandler<OnUpdateMainChainArgs>? OnUpdateMainChain
    {
        add { if (Inner is not null) Inner.OnUpdateMainChain += value; }
        remove { if (Inner is not null) Inner.OnUpdateMainChain -= value; }
    }
    public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs>? OnForkChoiceUpdated
    {
        add { if (Inner is not null) Inner.OnForkChoiceUpdated += value; }
        remove { if (Inner is not null) Inner.OnForkChoiceUpdated -= value; }
    }
    public event EventHandler<FinalizeEventArgs>? BlocksFinalized
    {
        add { if (Inner is not null) Inner.BlocksFinalized += value; }
        remove { if (Inner is not null) Inner.BlocksFinalized -= value; }
    }

    public void RaiseBlockAddedToMain(BlockReplacementEventArgs args) => _blockAddedToMain?.Invoke(this, args);

    public virtual Hash256 HeadHash => Inner?.HeadHash ?? Head?.Hash ?? Keccak.Zero;
    public virtual Hash256 GenesisHash => Inner?.GenesisHash ?? Keccak.Zero;
    public virtual Hash256? PendingHash => Inner?.PendingHash;
    public virtual Hash256? FinalizedHash => Inner?.FinalizedHash;
    public virtual Hash256? SafeHash => Inner?.SafeHash;
    public virtual ulong LastFinalizedBlockLevel => Inner?.LastFinalizedBlockLevel ?? 0UL;

    public virtual BlockHeader FindBestSuggestedHeader() =>
        Inner?.FindBestSuggestedHeader() ?? throw new NotImplementedException();

    public virtual Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
        Inner?.FindBlock(blockHash, options, blockNumber);
    public virtual Block? FindBlock(ulong blockNumber, BlockTreeLookupOptions options) =>
        Inner?.FindBlock(blockNumber, options);
    public virtual bool HasBlock(ulong blockNumber, Hash256 blockHash) => Inner?.HasBlock(blockNumber, blockHash) ?? false;
    public virtual BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
        Inner?.FindHeader(blockHash, options, blockNumber);
    public virtual BlockHeader? FindHeader(ulong blockNumber, BlockTreeLookupOptions options) =>
        Inner?.FindHeader(blockNumber, options);
    public virtual Hash256? FindBlockHash(ulong blockNumber) => Inner?.FindBlockHash(blockNumber);
    public virtual bool IsMainChain(BlockHeader blockHeader) => Inner?.IsMainChain(blockHeader) ?? false;
    public virtual bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) =>
        Inner?.IsMainChain(blockHash, throwOnMissingHash) ?? false;
    public virtual ulong GetLowestBlock() => Inner?.GetLowestBlock() ?? 0;

    public virtual ulong NetworkId => Inner?.NetworkId ?? 1;
    public virtual ulong ChainId => Inner?.ChainId ?? 1;
    public virtual BlockHeader? Genesis => Inner?.Genesis;
    public virtual Block? BestSuggestedBody => Inner?.BestSuggestedBody;
    public virtual BlockHeader? BestSuggestedBeaconHeader => Inner?.BestSuggestedBeaconHeader;
    public virtual BlockHeader? LowestInsertedHeader
    {
        get => Inner?.LowestInsertedHeader ?? _lowestInsertedHeader;
        set
        {
            if (Inner is not null) Inner.LowestInsertedHeader = value;
            _lowestInsertedHeader = value;
        }
    }
    public virtual BlockHeader? LowestInsertedBeaconHeader
    {
        get => Inner?.LowestInsertedBeaconHeader ?? _lowestInsertedBeaconHeader;
        set
        {
            if (Inner is not null) Inner.LowestInsertedBeaconHeader = value;
            _lowestInsertedBeaconHeader = value;
        }
    }
    public virtual ulong BestKnownNumber => Inner?.BestKnownNumber ?? Head?.Number ?? 0UL;
    public virtual ulong BestKnownBeaconNumber => Inner?.BestKnownBeaconNumber ?? 0UL;
    public virtual bool CanAcceptNewBlocks => Inner?.CanAcceptNewBlocks ?? true;
    public virtual (ulong BlockNumber, Hash256 BlockHash) SyncPivot
    {
        get => Inner?.SyncPivot ?? _syncPivot;
        set => _syncPivot = value;
    }
    public virtual bool IsProcessingBlock
    {
        get => Inner?.IsProcessingBlock ?? _isProcessingBlock;
        set
        {
            if (Inner is not null) Inner.IsProcessingBlock = value;
            _isProcessingBlock = value;
        }
    }

    public virtual AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        Inner?.Insert(header, headerOptions) ?? AddBlockResult.Added;

    public virtual void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        Inner?.BulkInsertHeader(headers, headerOptions);

    public virtual AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) =>
        Inner?.Insert(block, insertBlockOptions, insertHeaderOptions, bodiesWriteFlags) ?? AddBlockResult.Added;

    public virtual void UpdateHeadBlock(Hash256 blockHash) => Inner?.UpdateHeadBlock(blockHash);
    public virtual void NewOldestBlock(ulong oldestBlock) => Inner?.NewOldestBlock(oldestBlock);

    public virtual AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        Inner?.SuggestBlock(block, options) ?? AddBlockResult.Added;

    public virtual ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        Inner?.SuggestBlockAsync(block, options) ?? ValueTask.FromResult(SuggestBlock(block, options));

    public virtual AddBlockResult SuggestHeader(BlockHeader header) => Inner?.SuggestHeader(header) ?? AddBlockResult.Added;
    public virtual bool IsKnownBlock(ulong number, Hash256 blockHash) => Inner?.IsKnownBlock(number, blockHash) ?? false;
    public virtual bool IsKnownBeaconBlock(ulong number, Hash256 blockHash) => Inner?.IsKnownBeaconBlock(number, blockHash) ?? false;
    public virtual bool WasProcessed(ulong number, Hash256 blockHash) => Inner?.WasProcessed(number, blockHash) ?? false;

    public virtual bool TryUpdateMainChain(BlockHeader newHead, bool wereProcessed, bool forceUpdateHeadBlock = false, params ReadOnlySpan<Block> preloadedBlocks) =>
        Inner?.TryUpdateMainChain(newHead, wereProcessed, forceUpdateHeadBlock, preloadedBlocks) ?? true;

    public virtual void MarkChainAsProcessed(IReadOnlyList<Block> blocks) => Inner?.MarkChainAsProcessed(blocks);
    public virtual Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) =>
        Inner?.Accept(blockTreeVisitor, cancellationToken) ?? Task.CompletedTask;
    public virtual (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(ulong number, Hash256 blockHash) =>
        Inner?.GetInfo(number, blockHash) ?? (null, null);
    public virtual ChainLevelInfo? FindLevel(ulong number) => Inner?.FindLevel(number);
    public virtual BlockInfo FindCanonicalBlockInfo(ulong blockNumber) => Inner?.FindCanonicalBlockInfo(blockNumber) ?? null!;
    public virtual Hash256? FindHash(ulong blockNumber) => Inner?.FindHash(blockNumber);
    public virtual IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) =>
        Inner?.FindHeaders(hash, numberOfBlocks, skip, reverse) ?? new ArrayPoolList<BlockHeader>(0);
    public virtual void DeleteInvalidBlock(Block invalidBlock) => Inner?.DeleteInvalidBlock(invalidBlock);
    public virtual void ReportBadBlock(Block badBlock) => Inner?.ReportBadBlock(badBlock);
    public virtual void DeleteOldBlock(ulong blockNumber, Hash256 blockHash) => Inner?.DeleteOldBlock(blockNumber, blockHash);
    public virtual void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) =>
        Inner?.ForkChoiceUpdated(finalizedBlockHash, safeBlockBlockHash);
    public virtual int DeleteChainSlice(in ulong startNumber, ulong? endNumber = null, bool force = false) =>
        Inner?.DeleteChainSlice(startNumber, endNumber, force) ?? 0;
    public virtual bool IsBetterThanHead(BlockHeader? header) => Inner?.IsBetterThanHead(header) ?? false;
    public virtual void UpdateBeaconMainChain(IReadOnlyList<BlockInfo>? blockInfos, ulong clearBeaconMainChainStartPoint) =>
        Inner?.UpdateBeaconMainChain(blockInfos, clearBeaconMainChainStartPoint);
    public virtual void RecalculateTreeLevels() => Inner?.RecalculateTreeLevels();
}
