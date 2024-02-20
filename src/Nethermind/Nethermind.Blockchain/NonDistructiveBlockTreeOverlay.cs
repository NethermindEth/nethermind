// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public class NonDistructiveBlockTreeOverlay : IBlockTree
{
    private readonly IBlockTree _baseTree;
    private readonly IBlockTree _overlayTree;

    public NonDistructiveBlockTreeOverlay(IReadOnlyBlockTree baseTree, IBlockTree overlayTree)
    {
        _baseTree = baseTree ?? throw new ArgumentNullException(nameof(baseTree));
        _overlayTree = overlayTree ?? throw new ArgumentNullException(nameof(overlayTree));
        _overlayTree.UpdateMainChain(new[] { _baseTree.Head }, true, true);
    }

    public ulong NetworkId => _baseTree.NetworkId;
    public ulong ChainId => _baseTree.ChainId;
    public BlockHeader? Genesis => _baseTree.Genesis;

    public BlockHeader? BestSuggestedHeader => _overlayTree.BestSuggestedHeader ?? _baseTree.BestSuggestedHeader;
    public Block? BestSuggestedBody => _overlayTree.BestSuggestedBody ?? _baseTree.BestSuggestedBody;

    public BlockHeader? BestSuggestedBeaconHeader => _overlayTree.BestSuggestedBeaconHeader ?? _baseTree.BestSuggestedBeaconHeader;

    public BlockHeader? LowestInsertedHeader => _overlayTree.LowestInsertedHeader ?? _baseTree.LowestInsertedHeader;

    public long? LowestInsertedBodyNumber
    {
        get => _overlayTree.LowestInsertedBodyNumber ?? _baseTree.LowestInsertedBodyNumber;
        set => _overlayTree.LowestInsertedBodyNumber = value;
    }

    public BlockHeader? LowestInsertedBeaconHeader
    {
        get => _overlayTree.LowestInsertedBeaconHeader ?? _baseTree.LowestInsertedBeaconHeader;
        set => _overlayTree.LowestInsertedBeaconHeader = value;
    }

    public long BestKnownNumber => Math.Max(_overlayTree.BestKnownNumber, _baseTree.BestKnownNumber);
    public long BestKnownBeaconNumber => Math.Max(_overlayTree.BestKnownBeaconNumber, _baseTree.BestKnownBeaconNumber);
    public Hash256 HeadHash => _overlayTree.HeadHash ?? _baseTree.HeadHash;
    public Hash256 GenesisHash => _baseTree.GenesisHash;
    public Hash256? PendingHash => _overlayTree.PendingHash ?? _baseTree.PendingHash;
    public Hash256? FinalizedHash => _overlayTree.FinalizedHash ?? _baseTree.FinalizedHash;
    public Hash256? SafeHash => _overlayTree.SafeHash ?? _baseTree.SafeHash;
    public Block? Head => _overlayTree.Head ?? _baseTree.Head;
    public long? BestPersistedState { get => _overlayTree.BestPersistedState; set => _overlayTree.BestPersistedState = value; }

    public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
        _overlayTree.Insert(header, headerOptions);

    public AddBlockResult Insert(Block block,
        BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None,
        WriteFlags bodiesWriteFlags = WriteFlags.None) =>
        _overlayTree.Insert(block, insertBlockOptions, insertHeaderOptions, bodiesWriteFlags);

    public void UpdateHeadBlock(Hash256 blockHash) =>
        _overlayTree.UpdateHeadBlock(blockHash);

    public AddBlockResult SuggestBlock(Block block,
        BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        _overlayTree.SuggestBlock(block, options);

    public ValueTask<AddBlockResult> SuggestBlockAsync(Block block,
        BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
        _overlayTree.SuggestBlockAsync(block, options);

    public AddBlockResult SuggestHeader(BlockHeader header) => _overlayTree.SuggestHeader(header);

    public bool IsKnownBlock(long number, Hash256 blockHash) => _overlayTree.IsKnownBlock(number, blockHash) || _baseTree.IsKnownBlock(number, blockHash);

    public bool IsKnownBeaconBlock(long number, Hash256 blockHash) => _overlayTree.IsKnownBeaconBlock(number, blockHash) || _baseTree.IsKnownBeaconBlock(number, blockHash);

    public bool WasProcessed(long number, Hash256 blockHash) => _overlayTree.WasProcessed(number, blockHash) || _baseTree.WasProcessed(number, blockHash);

    public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) =>
        _overlayTree.UpdateMainChain(blocks, wereProcessed, forceHeadBlock);

    public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) => _overlayTree.MarkChainAsProcessed(blocks);

    public bool CanAcceptNewBlocks => _overlayTree.CanAcceptNewBlocks;

    public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => _overlayTree.Accept(blockTreeVisitor, cancellationToken);

    public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash)
    {
        (BlockInfo Info, ChainLevelInfo Level) overlayInfo = _overlayTree.GetInfo(number, blockHash);
        return overlayInfo.Info is not null || overlayInfo.Level is not null ? overlayInfo : _baseTree.GetInfo(number, blockHash);
    }

    public ChainLevelInfo? FindLevel(long number) => _overlayTree.FindLevel(number) ?? _baseTree.FindLevel(number);

    public BlockInfo FindCanonicalBlockInfo(long blockNumber) => _overlayTree.FindCanonicalBlockInfo(blockNumber) ?? _baseTree.FindCanonicalBlockInfo(blockNumber);

    public Hash256 FindHash(long blockNumber) => _overlayTree.FindHash(blockNumber) ?? _baseTree.FindHash(blockNumber);

    public BlockHeader[] FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse)
    {
        BlockHeader[] overlayHeaders = _overlayTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
        return overlayHeaders.Length > 0 ? overlayHeaders : _baseTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
    }

    public BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth) =>
        _overlayTree.FindLowestCommonAncestor(firstDescendant, secondDescendant, maxSearchDepth) ?? _baseTree.FindLowestCommonAncestor(firstDescendant, secondDescendant, maxSearchDepth);

    public void DeleteInvalidBlock(Block invalidBlock) =>
        _overlayTree.DeleteInvalidBlock(invalidBlock);

    public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) =>
        _overlayTree.ForkChoiceUpdated(finalizedBlockHash, safeBlockBlockHash);

    // Event forwarding
    public event EventHandler<BlockEventArgs>? NewBestSuggestedBlock
    {
        add
        {
            if (value is not null)
            {
                _baseTree.NewBestSuggestedBlock += value;
                _overlayTree.NewBestSuggestedBlock += value;
            }
        }
        remove
        {
            if (value is not null)
            {
                _baseTree.NewBestSuggestedBlock -= value;
                _overlayTree.NewBestSuggestedBlock -= value;
            }
        }
    }

    public event EventHandler<BlockEventArgs>? NewSuggestedBlock
    {
        add
        {
            if (value is not null)
            {
                _baseTree.NewSuggestedBlock += value;
                _overlayTree.NewSuggestedBlock += value;
            }
        }
        remove
        {
            if (value is not null)
            {
                _baseTree.NewSuggestedBlock -= value;
                _overlayTree.NewSuggestedBlock -= value;
            }
        }
    }

    public event EventHandler<BlockReplacementEventArgs>? BlockAddedToMain
    {
        add
        {
            if (value is not null)
            {
                _baseTree.BlockAddedToMain += value;
                _overlayTree.BlockAddedToMain += value;
            }
        }
        remove
        {
            if (value is not null)
            {
                _baseTree.BlockAddedToMain -= value;
                _overlayTree.BlockAddedToMain -= value;
            }
        }
    }

    public event EventHandler<BlockEventArgs>? NewHeadBlock
    {
        add
        {
            if (value is not null)
            {
                _baseTree.NewHeadBlock += value;
                _overlayTree.NewHeadBlock += value;
            }
        }
        remove
        {
            if (value is not null)
            {
                _baseTree.NewHeadBlock -= value;
                _overlayTree.NewHeadBlock -= value;
            }
        }
    }

    public event EventHandler<OnUpdateMainChainArgs>? OnUpdateMainChain
    {
        add
        {
            if (value is not null)
            {
                _baseTree.OnUpdateMainChain += value;
                _overlayTree.OnUpdateMainChain += value;
            }
        }
        remove
        {
            if (value is not null)
            {
                _baseTree.OnUpdateMainChain -= value;
                _overlayTree.OnUpdateMainChain -= value;
            }
        }
    }

    public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) =>
        _overlayTree.DeleteChainSlice(startNumber, endNumber, force);

    public bool IsBetterThanHead(BlockHeader? header) => _overlayTree.IsBetterThanHead(header) || _baseTree.IsBetterThanHead(header);

    public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint) =>
        _overlayTree.UpdateBeaconMainChain(blockInfos, clearBeaconMainChainStartPoint);

    public void RecalculateTreeLevels() => _overlayTree.RecalculateTreeLevels();

    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        _overlayTree.FindBlock(blockHash, options, blockNumber) ?? _baseTree.FindBlock(blockHash, options, blockNumber);

    public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) =>
        _overlayTree.FindBlock(blockNumber, options) ?? _baseTree.FindBlock(blockNumber, options);

    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        _overlayTree.FindHeader(blockHash, options, blockNumber) ?? _baseTree.FindHeader(blockHash, options, blockNumber);

    public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options) =>
        _overlayTree.FindHeader(blockNumber, options) ?? _baseTree.FindHeader(blockNumber, options);

    public Hash256? FindBlockHash(long blockNumber) =>
        _overlayTree.FindBlockHash(blockNumber) ?? _baseTree.FindBlockHash(blockNumber);

    public bool IsMainChain(BlockHeader blockHeader) =>
        _baseTree.IsMainChain(blockHeader) || _overlayTree.IsMainChain(blockHeader);

    public bool IsMainChain(Hash256 blockHash)
    {
        try
        {
            if (_baseTree.IsMainChain(blockHash)) return true;
        }
        catch
        {
            // ignored as we have _overlayTree to look into
        }

        return _overlayTree.IsMainChain(blockHash);
    }

    public BlockHeader FindBestSuggestedHeader()
    {
        BlockHeader? overlayHeader = _overlayTree.FindBestSuggestedHeader();
        return overlayHeader ?? _baseTree.FindBestSuggestedHeader();
    }

}
