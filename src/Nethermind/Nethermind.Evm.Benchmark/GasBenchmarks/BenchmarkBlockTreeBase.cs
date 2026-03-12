// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Abstract base for IBlockTree stubs used in gas benchmarks.
/// Provides no-op implementations of all mutation methods and returns null/default for all lookups.
/// Subclasses override lookup methods to return parent header data as needed.
/// </summary>
internal abstract class BenchmarkBlockTreeBase : IBlockTree
{
    public virtual Hash256 HeadHash => null;
    public virtual Hash256 GenesisHash => null;
    public Hash256 PendingHash => null;
    public Hash256 FinalizedHash => null;
    public Hash256 SafeHash => null;
    public virtual Block Head => null;
    public virtual ulong NetworkId => 1;
    public virtual ulong ChainId => 1;
    public virtual BlockHeader Genesis => null;
    public BlockHeader BestSuggestedHeader { get; set; }
    public virtual Block BestSuggestedBody => null;
    public virtual BlockHeader BestSuggestedBeaconHeader => null;
    public BlockHeader LowestInsertedHeader { get; set; }
    public BlockHeader LowestInsertedBeaconHeader { get; set; }
    public virtual long BestKnownNumber => 0;
    public virtual long BestKnownBeaconNumber => 0;
    public bool CanAcceptNewBlocks => true;
    public (long BlockNumber, Hash256 BlockHash) SyncPivot { get; set; }
    public bool IsProcessingBlock { get; set; }
    public long? BestPersistedState { get; set; }

    public event EventHandler<BlockEventArgs> NewBestSuggestedBlock { add { } remove { } }
    public event EventHandler<BlockEventArgs> NewSuggestedBlock { add { } remove { } }
    public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain { add { } remove { } }
    public event EventHandler<BlockEventArgs> NewHeadBlock { add { } remove { } }
    public event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain { add { } remove { } }
    public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs> OnForkChoiceUpdated { add { } remove { } }

    public virtual Block FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
    public virtual Block FindBlock(long blockNumber, BlockTreeLookupOptions options) => null;
    public virtual bool HasBlock(long blockNumber, Hash256 blockHash) => false;
    public virtual BlockHeader FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
    public virtual BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) => null;
    public virtual Hash256 FindBlockHash(long blockNumber) => null;
    public virtual bool IsMainChain(BlockHeader blockHeader) => false;
    public virtual bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => false;
    public virtual BlockHeader FindBestSuggestedHeader() => BestSuggestedHeader;
    public virtual long GetLowestBlock() => 0;

    public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) => AddBlockResult.Added;
    public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) { }
    public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None, BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) => AddBlockResult.Added;
    public void UpdateHeadBlock(Hash256 blockHash) { }
    public void NewOldestBlock(long oldestBlock) { }
    public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => AddBlockResult.Added;
    public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => ValueTask.FromResult(AddBlockResult.Added);
    public AddBlockResult SuggestHeader(BlockHeader header) => AddBlockResult.Added;

    public virtual bool IsKnownBlock(long number, Hash256 blockHash) => false;
    public virtual bool IsKnownBeaconBlock(long number, Hash256 blockHash) => false;
    public virtual bool WasProcessed(long number, Hash256 blockHash) => false;

    public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) { }
    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) { }
    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Hash256 blockHash) => (null, null);
    public virtual ChainLevelInfo FindLevel(long number) => null;
    public virtual BlockInfo FindCanonicalBlockInfo(long blockNumber) => null;
    public virtual Hash256 FindHash(long blockNumber) => null;

    public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => new ArrayPoolList<BlockHeader>(0);
    public void DeleteInvalidBlock(Block invalidBlock) { }
    public void DeleteOldBlock(long blockNumber, Hash256 blockHash) { }
    public void ForkChoiceUpdated(Hash256 finalizedBlockHash, Hash256 safeBlockBlockHash) { }
    public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) => 0;
    public virtual bool IsBetterThanHead(BlockHeader header) => true;
    public void UpdateBeaconMainChain(BlockInfo[] blockInfos, long clearBeaconMainChainStartPoint) { }
    public void RecalculateTreeLevels() { }
}
