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

/// <summary>
/// This class is part of the StatelessExecution tool. It's intended to be used only inside the processing pipeline.
/// </summary>
public class StatelessBlockTree(IReadOnlyCollection<BlockHeader> headers)
    : IBlockTree, IBlockhashCache
{
    private readonly Dictionary<Hash256AsKey, BlockHeader> _hashToHeader =
        headers.ToDictionary(header => (Hash256AsKey)(header.Hash ?? throw new ArgumentNullException(nameof(header.Hash))), header => header);

    private readonly Dictionary<ulong, BlockHeader> _numberToHeader =
        headers.ToDictionary(header => header.Number, header => header);

    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
        throw new NotSupportedException();

    public Block? FindBlock(ulong blockNumber, BlockTreeLookupOptions options) =>
        throw new NotSupportedException();

    public bool HasBlock(ulong blockNumber, Hash256 blockHash) =>
        throw new NotSupportedException();

    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null)
         => _hashToHeader.GetValueOrDefault(blockHash);

    public BlockHeader? FindHeader(ulong blockNumber, BlockTreeLookupOptions options)
        => _numberToHeader.GetValueOrDefault(blockNumber);

    public Hash256? FindBlockHash(ulong blockNumber)
        => _numberToHeader.GetValueOrDefault(blockNumber)?.Hash;

    public bool IsMainChain(BlockHeader blockHeader)
        => blockHeader.Hash is not null && _hashToHeader.ContainsKey(blockHeader.Hash);

    public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true)
        => _hashToHeader.ContainsKey(blockHash) ? true : throw new InvalidOperationException();

    public BlockHeader FindBestSuggestedHeader()
        => throw new NotSupportedException();

    public ulong GetLowestBlock()
        => throw new NotSupportedException();

    public Hash256 HeadHash => throw new NotSupportedException();
    public Hash256 GenesisHash => throw new NotSupportedException();
    public Hash256? PendingHash => throw new NotSupportedException();
    public Hash256? FinalizedHash => throw new NotSupportedException();
    public Hash256? SafeHash => throw new NotSupportedException();
    public Block? Head => throw new NotSupportedException();

    public ulong? BestPersistedState
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

    public ulong BestKnownNumber => throw new NotSupportedException();
    public ulong BestKnownBeaconNumber => throw new NotSupportedException();

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

    public bool IsKnownBlock(ulong number, Hash256 blockHash)
        => throw new NotSupportedException();

    public bool IsKnownBeaconBlock(ulong number, Hash256 blockHash)
        => throw new NotSupportedException();

    public bool WasProcessed(ulong number, Hash256 blockHash)
        => throw new NotSupportedException();

    public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false)
        => throw new NotSupportedException();

    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks)
        => throw new NotSupportedException();

    public bool CanAcceptNewBlocks => throw new NotSupportedException();
    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(ulong number, Hash256 blockHash)
        => throw new NotSupportedException();

    public ChainLevelInfo? FindLevel(ulong number)
        => throw new NotSupportedException();

    public BlockInfo? FindCanonicalBlockInfo(ulong blockNumber)
        => throw new NotSupportedException();

    public Hash256? FindHash(ulong blockNumber)
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
    public int DeleteChainSlice(in ulong startNumber, ulong? endNumber = null, bool force = false)
        => throw new NotSupportedException();

    public bool IsBetterThanHead(BlockHeader? header)
        => throw new NotSupportedException();

    public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, ulong clearBeaconMainChainStartPoint)
        => throw new NotSupportedException();

    public void RecalculateTreeLevels()
        => throw new NotSupportedException();

    public (ulong BlockNumber, Hash256 BlockHash) SyncPivot
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public bool IsProcessingBlock
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void NewOldestBlock(ulong oldestBlock) => throw new NotImplementedException();

    public void DeleteOldBlock(ulong blockNumber, Hash256 blockHash) => throw new NotImplementedException();

    public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs>? OnForkChoiceUpdated
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }

    public Hash256? GetHash(BlockHeader headBlock, int depth) =>
        depth == 0
            ? headBlock.Hash
            : _numberToHeader.TryGetValue(headBlock.Number - (ulong)depth, out BlockHeader? header)
                ? header?.Hash
                : null;

    public Task<Hash256[]?> Prefetch(BlockHeader blockHeader, CancellationToken cancellationToken)
    {
        const int length = BlockhashCache.MaxDepth + 1;
        Hash256[] result = new Hash256[length];
        result[0] = blockHeader.Hash;
        for (int i = 1; i < length; i++)
        {
            if (_numberToHeader.TryGetValue(blockHeader.Number - (ulong)i, out BlockHeader header))
            {
                result[i] = header.Hash;
            }
        }

        return Task.FromResult(result);
    }
}
