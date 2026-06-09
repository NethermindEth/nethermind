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
/// Hand-rolled <see cref="IBlockTree"/> fake for commit-block tests. Castle DynamicProxy (NSubstitute)
/// throws <see cref="InvalidProgramException"/> when proxying <see cref="IBlockTree.TryUpdateMainChain"/>
/// because it takes <c>params ReadOnlySpan&lt;Block&gt;</c>.
/// </summary>
internal sealed class RecordingCommitBlockTree : IBlockTree
{
    public Block? Head { get; set; }
    public AddBlockResult SuggestResult { get; set; } = AddBlockResult.Added;

    public Block? SuggestedBlock { get; private set; }
    public BlockTreeSuggestOptions SuggestOptions { get; private set; }
    public bool? TryUpdateWereProcessed { get; private set; }
    public bool? TryUpdateForceUpdateHeadBlock { get; private set; }
    public Block[]? TryUpdatePreloadedBlocks { get; private set; }

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

    public BlockHeader FindBestSuggestedHeader() => throw new NotImplementedException();

    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
    public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) => null;
    public bool HasBlock(long blockNumber, Hash256 blockHash) => false;
    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
    public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options) => null;
    public Hash256? FindBlockHash(long blockNumber) => null;
    public bool IsMainChain(BlockHeader blockHeader) => false;
    public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => false;
    public long GetLowestBlock() => 0;

    public ulong NetworkId => 1;
    public ulong ChainId => 1;
    public BlockHeader? Genesis => null;
    public BlockHeader? BestSuggestedHeader => null;
    public Block? BestSuggestedBody => null;
    public BlockHeader? BestSuggestedBeaconHeader => null;
    public BlockHeader? LowestInsertedHeader { get; set; }
    public BlockHeader? LowestInsertedBeaconHeader { get; set; }
    public long BestKnownNumber => Head?.Number ?? 0;
    public long BestKnownBeaconNumber => 0;
    public bool CanAcceptNewBlocks => true;
    public (long BlockNumber, Hash256 BlockHash) SyncPivot { get; set; }
    public bool IsProcessingBlock { get; set; }

    public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        AddBlockResult.Added;

    public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) { }

    public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) =>
        AddBlockResult.Added;

    public void UpdateHeadBlock(Hash256 blockHash) { }
    public void NewOldestBlock(long oldestBlock) { }

    public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        SuggestedBlock = block;
        SuggestOptions = options;
        return SuggestResult;
    }

    public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        ValueTask.FromResult(SuggestBlock(block, options));

    public AddBlockResult SuggestHeader(BlockHeader header) => AddBlockResult.Added;
    public bool IsKnownBlock(long number, Hash256 blockHash) => false;
    public bool IsKnownBeaconBlock(long number, Hash256 blockHash) => false;
    public bool WasProcessed(long number, Hash256 blockHash) => false;

    public bool TryUpdateMainChain(BlockHeader newHead, bool wereProcessed, bool forceUpdateHeadBlock = false, params ReadOnlySpan<Block> preloadedBlocks)
    {
        TryUpdateWereProcessed = wereProcessed;
        TryUpdateForceUpdateHeadBlock = forceUpdateHeadBlock;
        TryUpdatePreloadedBlocks = preloadedBlocks.ToArray();
        return true;
    }

    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) { }
    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => Task.CompletedTask;
    public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash) => (null, null);
    public ChainLevelInfo? FindLevel(long number) => null;
    public BlockInfo FindCanonicalBlockInfo(long blockNumber) => null!;
    public Hash256? FindHash(long blockNumber) => null;
    public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) =>
        new ArrayPoolList<BlockHeader>(0);

    public void DeleteInvalidBlock(Block invalidBlock) { }
    public void ReportBadBlock(Block badBlock) { }
    public void DeleteOldBlock(long blockNumber, Hash256 blockHash) { }
    public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) { }
    public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) => 0;
    public bool IsBetterThanHead(BlockHeader? header) => false;
    public void UpdateBeaconMainChain(IReadOnlyList<BlockInfo>? blockInfos, long clearBeaconMainChainStartPoint) { }
    public void RecalculateTreeLevels() { }
}
