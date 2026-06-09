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
/// No-op <see cref="IBlockTree"/> stub with safe defaults for unit tests that only exercise a few members.
/// </summary>
internal class MinimalBlockTreeStub : IBlockTree
{
    public Block? Head { get; set; }

    public event EventHandler<BlockReplacementEventArgs>? BlockAddedToMain;
    public event EventHandler<BlockEventArgs>? NewBestSuggestedBlock { add { } remove { } }
    public event EventHandler<BlockEventArgs>? NewSuggestedBlock { add { } remove { } }
    public event EventHandler<BlockEventArgs>? NewHeadBlock { add { } remove { } }
    public event EventHandler<OnUpdateMainChainArgs>? OnUpdateMainChain { add { } remove { } }
    public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs>? OnForkChoiceUpdated { add { } remove { } }

    public Hash256 HeadHash => Head?.Hash ?? Keccak.Zero;
    public Hash256 GenesisHash => Keccak.Zero;
    public Hash256? PendingHash => null;
    public Hash256? FinalizedHash => null;
    public Hash256? SafeHash => null;
    public long? BestPersistedState { get; set; }

    public virtual BlockHeader FindBestSuggestedHeader() => throw new NotImplementedException();

    public virtual Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
    public virtual Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) => null;
    public virtual bool HasBlock(long blockNumber, Hash256 blockHash) => false;
    public virtual BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
    public virtual BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options) => null;
    public virtual Hash256? FindBlockHash(long blockNumber) => null;
    public virtual bool IsMainChain(BlockHeader blockHeader) => false;
    public virtual bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => false;
    public virtual long GetLowestBlock() => 0;

    public virtual ulong NetworkId => 1;
    public virtual ulong ChainId => 1;
    public virtual BlockHeader? Genesis => null;
    public virtual BlockHeader? BestSuggestedHeader => null;
    public virtual Block? BestSuggestedBody => null;
    public virtual BlockHeader? BestSuggestedBeaconHeader => null;
    public virtual BlockHeader? LowestInsertedHeader { get; set; }
    public virtual BlockHeader? LowestInsertedBeaconHeader { get; set; }
    public virtual long BestKnownNumber => Head?.Number ?? 0;
    public virtual long BestKnownBeaconNumber => 0;
    public virtual bool CanAcceptNewBlocks => true;
    public virtual (long BlockNumber, Hash256 BlockHash) SyncPivot { get; set; }
    public virtual bool IsProcessingBlock { get; set; }

    public virtual AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        AddBlockResult.Added;

    public virtual void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) { }

    public virtual AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) =>
        AddBlockResult.Added;

    public virtual void UpdateHeadBlock(Hash256 blockHash) { }
    public virtual void NewOldestBlock(long oldestBlock) { }

    public virtual AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        AddBlockResult.Added;

    public virtual ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        ValueTask.FromResult(SuggestBlock(block, options));

    public virtual AddBlockResult SuggestHeader(BlockHeader header) => AddBlockResult.Added;
    public virtual bool IsKnownBlock(long number, Hash256 blockHash) => false;
    public virtual bool IsKnownBeaconBlock(long number, Hash256 blockHash) => false;
    public virtual bool WasProcessed(long number, Hash256 blockHash) => false;

    public virtual bool TryUpdateMainChain(BlockHeader newHead, bool wereProcessed, bool forceUpdateHeadBlock = false, params ReadOnlySpan<Block> preloadedBlocks) =>
        true;

    public virtual void MarkChainAsProcessed(IReadOnlyList<Block> blocks) { }
    public virtual Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash) => (null, null);
    public virtual ChainLevelInfo? FindLevel(long number) => null;
    public virtual BlockInfo FindCanonicalBlockInfo(long blockNumber) => null!;
    public virtual Hash256? FindHash(long blockNumber) => null;
    public virtual IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) =>
        new ArrayPoolList<BlockHeader>(0);

    public virtual void DeleteInvalidBlock(Block invalidBlock) { }
    public virtual void ReportBadBlock(Block badBlock) { }
    public virtual void DeleteOldBlock(long blockNumber, Hash256 blockHash) { }
    public virtual void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) { }
    public virtual int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) => 0;
    public virtual bool IsBetterThanHead(BlockHeader? header) => false;
    public virtual void UpdateBeaconMainChain(IReadOnlyList<BlockInfo>? blockInfos, long clearBeaconMainChainStartPoint) { }
    public virtual void RecalculateTreeLevels() { }
}
