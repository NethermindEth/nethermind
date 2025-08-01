// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

public class StatelessBlockTree(BlockHeader[] headers) : IBlockTree
{
    private readonly Dictionary<Hash256, BlockHeader> _hashToHeader =
        headers.ToDictionary(header => header.Hash ?? throw new ArgumentNullException(), header => header);

    private readonly Dictionary<long, BlockHeader> _numberToHeader =
        headers.ToDictionary(header => header.Number, header => header);

    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        throw new NotSupportedException();

    public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) =>
        throw new NotSupportedException();

    public bool HasBlock(long blockNumber, Hash256 blockHash) =>
        throw new NotSupportedException();

    // TODO: why we have blockNumber here?
    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
         => _hashToHeader.GetValueOrDefault(blockHash);

    public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options)
        => _numberToHeader.GetValueOrDefault(blockNumber);

    public Hash256? FindBlockHash(long blockNumber)
        => _numberToHeader.GetValueOrDefault(blockNumber)?.Hash;

    public bool IsMainChain(BlockHeader blockHeader)
        => blockHeader.Hash is not null && _hashToHeader.ContainsKey(blockHeader.Hash);

    public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true)
        => _hashToHeader.ContainsKey(blockHash) ? true : throw new InvalidOperationException();

    public BlockHeader FindBestSuggestedHeader()
        => throw new NotSupportedException();

    public long GetLowestBlock()
        => throw new NotSupportedException();

    public Hash256 HeadHash => throw new NotSupportedException();
    public Hash256 GenesisHash => throw new NotSupportedException();
    public Hash256? PendingHash => throw new NotSupportedException();
    public Hash256? FinalizedHash => throw new NotSupportedException();
    public Hash256? SafeHash => throw new NotSupportedException();
    public Block? Head => throw new NotSupportedException();

    public long? BestPersistedState
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public ulong NetworkId => throw new NotSupportedException();
    public ulong ChainId => throw new NotSupportedException();
    public BlockHeader? Genesis => throw new NotSupportedException();
    public BlockHeader? BestSuggestedHeader => throw new NotSupportedException();
    public Block? BestSuggestedBody => throw new NotSupportedException();
    public BlockHeader? BestSuggestedBeaconHeader => throw new NotSupportedException();

    public BlockHeader? LowestInsertedHeader
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public BlockHeader? LowestInsertedBeaconHeader
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public long BestKnownNumber => throw new NotSupportedException();
    public long BestKnownBeaconNumber => throw new NotSupportedException();

    public AddBlockResult Insert(BlockHeader header,
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None)
        => throw new NotSupportedException();

    public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers,
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None)
        => throw new NotSupportedException();

    public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None,
        WriteFlags bodiesWriteFlags = WriteFlags.None)
        => throw new NotSupportedException();

    public void UpdateHeadBlock(Hash256 blockHash)
        => throw new NotSupportedException();

    public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        => throw new NotSupportedException();

    public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        => throw new NotSupportedException();

    public AddBlockResult SuggestHeader(BlockHeader header)
        => throw new NotSupportedException();

    public bool IsKnownBlock(long number, Hash256 blockHash)
        => throw new NotSupportedException();

    public bool IsKnownBeaconBlock(long number, Hash256 blockHash)
        => throw new NotSupportedException();

    public bool WasProcessed(long number, Hash256 blockHash)
        => throw new NotSupportedException();

    public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false)
        => throw new NotSupportedException();

    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks)
        => throw new NotSupportedException();

    public bool CanAcceptNewBlocks => throw new NotSupportedException();
    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash)
        => throw new NotSupportedException();

    public ChainLevelInfo? FindLevel(long number)
        => throw new NotSupportedException();

    public BlockInfo FindCanonicalBlockInfo(long blockNumber)
        => throw new NotSupportedException();

    public Hash256 FindHash(long blockNumber)
        => throw new NotSupportedException();

    public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse)
        => throw new NotSupportedException();

    public void DeleteInvalidBlock(Block invalidBlock)
        => throw new NotSupportedException();

    public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash)
        => throw new NotSupportedException();

    public event EventHandler<BlockEventArgs>? NewBestSuggestedBlock
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }
    public event EventHandler<BlockEventArgs>? NewSuggestedBlock
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }
    public event EventHandler<BlockReplacementEventArgs>? BlockAddedToMain
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }
    public event EventHandler<BlockEventArgs>? NewHeadBlock
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }
    public event EventHandler<OnUpdateMainChainArgs>? OnUpdateMainChain
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }
    public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false)
        => throw new NotSupportedException();

    public bool IsBetterThanHead(BlockHeader? header)
        => throw new NotSupportedException();

    public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint)
        => throw new NotSupportedException();

    public void RecalculateTreeLevels()
        => throw new NotSupportedException();

    public (long BlockNumber, Hash256 BlockHash) SyncPivot {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public bool IsProcessingBlock
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
