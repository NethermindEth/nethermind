// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// Safe to be reused for all classes reading the same wrapped block tree.
    /// </summary>
    public class ReadOnlyBlockTree(IBlockTree wrapped) : IReadOnlyBlockTree
    {
        public ulong NetworkId => wrapped.NetworkId;
        public ulong ChainId => wrapped.ChainId;
        public BlockHeader? Genesis => wrapped.Genesis;
        public BlockHeader? BestSuggestedHeader => wrapped.BestSuggestedHeader;
        public BlockHeader? BestSuggestedBeaconHeader => wrapped.BestSuggestedBeaconHeader;
        public BlockHeader? LowestInsertedHeader => wrapped.LowestInsertedHeader;

        public long? LowestInsertedBodyNumber
        {
            get => wrapped.LowestInsertedBodyNumber;
            set => wrapped.LowestInsertedBodyNumber = value;
        }

        public long? BestPersistedState
        {
            get => wrapped.BestPersistedState;
            set => wrapped.BestPersistedState = value;
        }


        public BlockHeader? LowestInsertedBeaconHeader
        {
            get => wrapped.LowestInsertedBeaconHeader;
            set => wrapped.LowestInsertedBeaconHeader = value;
        }

        public Block? BestSuggestedBody => wrapped.BestSuggestedBody;
        public long BestKnownNumber => wrapped.BestKnownNumber;
        public long BestKnownBeaconNumber => wrapped.BestKnownBeaconNumber;
        public Block? Head => wrapped.Head;
        public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MarkChainAsProcessed)} calls");
        public (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Hash256 blockHash) => wrapped.GetInfo(number, blockHash);
        public bool CanAcceptNewBlocks { get; } = false;

        public async Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
        {
            await wrapped.Accept(blockTreeVisitor, cancellationToken);
        }

        public ChainLevelInfo? FindLevel(long number) => wrapped.FindLevel(number);
        public BlockInfo FindCanonicalBlockInfo(long blockNumber) => wrapped.FindCanonicalBlockInfo(blockNumber);

        public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None, BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags blockWriteFlags = WriteFlags.None) =>
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public void Insert(IEnumerable<Block> blocks) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public void UpdateHeadBlock(Hash256 blockHash)
        {
            // hacky while there is not special tree for RPC
            wrapped.UpdateHeadBlock(blockHash);
        }

        public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlock)} calls");

        public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlockAsync)} calls");

        public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public AddBlockResult SuggestHeader(BlockHeader header) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestHeader)} calls");

        public Hash256 HeadHash => wrapped.HeadHash;
        public Hash256 GenesisHash => wrapped.GenesisHash;
        public Hash256? PendingHash => wrapped.PendingHash;
        public Hash256? FinalizedHash => wrapped.FinalizedHash;
        public Hash256? SafeHash => wrapped.SafeHash;

        public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => wrapped.FindBlock(blockHash, options, blockNumber);

        public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => wrapped.FindHeader(blockHash, options, blockNumber: blockNumber);

        public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options) => wrapped.FindHeader(blockNumber, options);
        public Hash256? FindBlockHash(long blockNumber) => wrapped.FindBlockHash(blockNumber);

        public bool IsMainChain(BlockHeader blockHeader) => wrapped.IsMainChain(blockHeader);

        public Hash256 FindHash(long blockNumber) => wrapped.FindHash(blockNumber);

        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => wrapped.FindHeaders(hash, numberOfBlocks, skip, reverse);

        public BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth) => wrapped.FindLowestCommonAncestor(firstDescendant, secondDescendant, maxSearchDepth);

        public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options) => wrapped.FindBlock(blockNumber, options);

        public void DeleteInvalidBlock(Block invalidBlock) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(DeleteInvalidBlock)} calls");

        public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => wrapped.IsMainChain(blockHash, throwOnMissingHash);

        public BlockHeader FindBestSuggestedHeader() => wrapped.FindBestSuggestedHeader();

        public bool IsKnownBlock(long number, Hash256 blockHash) => wrapped.IsKnownBlock(number, blockHash);

        public bool IsKnownBeaconBlock(long number, Hash256 blockHash) => wrapped.IsKnownBeaconBlock(number, blockHash);

        public bool WasProcessed(long number, Hash256 blockHash) => wrapped.WasProcessed(number, blockHash);

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockEventArgs> NewSuggestedBlock
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockEventArgs> NewHeadBlock
        {
            add { }
            remove { }
        }

        public event EventHandler<OnUpdateMainChainArgs>? OnUpdateMainChain
        {
            add { }
            remove { }
        }

        public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false)
        {
            var bestKnownNumber = BestKnownNumber;
            if (endNumber is null || endNumber == bestKnownNumber)
            {
                if (Head?.Number > 0)
                {
                    if (Head.Number < startNumber)
                    {
                        const long searchLimit = 2;
                        long endSearch = Math.Min(bestKnownNumber, startNumber + searchLimit - 1);

                        IEnumerable<BlockHeader?> GetPotentiallyCorruptedBlocks(long start)
                        {
                            for (long i = start; i <= endSearch; i++)
                            {
                                yield return wrapped.FindHeader(i, BlockTreeLookupOptions.None);
                            }
                        }

                        if (force || GetPotentiallyCorruptedBlocks(startNumber).Any(b => b is null))
                        {
                            return wrapped.DeleteChainSlice(startNumber, endNumber, force);
                        }

                        throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} cannot {nameof(DeleteChainSlice)} if searched blocks [{startNumber}, {endSearch}] are not corrupted.");
                    }

                    throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} cannot {nameof(DeleteChainSlice)} if {nameof(startNumber)} is not past {nameof(Head)}.");
                }

                throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} cannot {nameof(DeleteChainSlice)} if {nameof(Head)} is not past Genesis.");
            }

            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(DeleteChainSlice)} calls with {nameof(endNumber)} other than {nameof(BestKnownNumber)} specified.");

        }

        public bool IsBetterThanHead(BlockHeader? header) => wrapped.IsBetterThanHead(header);
        public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(UpdateBeaconMainChain)} calls");
        public void RecalculateTreeLevels() => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(RecalculateTreeLevels)} calls");

        public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(UpdateMainChain)} calls");

        public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockBlockHash) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(ForkChoiceUpdated)} calls");
    }
}
