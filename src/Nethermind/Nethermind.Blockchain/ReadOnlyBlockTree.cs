// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// Safe to be reused for all classes reading the same wrapped block tree.
    /// </summary>
    public class ReadOnlyBlockTree : IReadOnlyBlockTree
    {
        private readonly IBlockTree _wrapped;

        public ReadOnlyBlockTree(IBlockTree wrapped)
        {
            _wrapped = wrapped;
        }

        public ulong NetworkId => _wrapped.NetworkId;
        public ulong ChainId => _wrapped.ChainId;
        public BlockHeader Genesis => _wrapped.Genesis;
        public BlockHeader BestSuggestedHeader => _wrapped.BestSuggestedHeader;
        public BlockHeader BestSuggestedBeaconHeader => _wrapped.BestSuggestedBeaconHeader;
        public BlockHeader LowestInsertedHeader => _wrapped.LowestInsertedHeader;

        public long? LowestInsertedBodyNumber
        {
            get => _wrapped.LowestInsertedBodyNumber;
            set => _wrapped.LowestInsertedBodyNumber = value;
        }

        public long? BestPersistedState
        {
            get => _wrapped.BestPersistedState;
            set => _wrapped.BestPersistedState = value;
        }


        public BlockHeader? LowestInsertedBeaconHeader
        {
            get => _wrapped.LowestInsertedBeaconHeader;
            set => _wrapped.LowestInsertedBeaconHeader = value;
        }

        public Block BestSuggestedBody => _wrapped.BestSuggestedBody;
        public long BestKnownNumber => _wrapped.BestKnownNumber;
        public long BestKnownBeaconNumber => _wrapped.BestKnownBeaconNumber;
        public Block Head => _wrapped.Head;
        public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MarkChainAsProcessed)} calls");
        public (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Keccak blockHash) => _wrapped.GetInfo(number, blockHash);
        public UInt256? UpdateTotalDifficulty(Block block, UInt256 totalDifficulty) => throw new InvalidOperationException();
        public bool CanAcceptNewBlocks { get; } = false;

        public async Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
        {
            await _wrapped.Accept(blockTreeVisitor, cancellationToken);
        }

        public ChainLevelInfo FindLevel(long number) => _wrapped.FindLevel(number);
        public BlockInfo FindCanonicalBlockInfo(long blockNumber) => _wrapped.FindCanonicalBlockInfo(blockNumber);

        public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None, BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None) =>
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public void Insert(IEnumerable<Block> blocks) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public void UpdateHeadBlock(Keccak blockHash)
        {
            // hacky while there is not special tree for RPC
            _wrapped.UpdateHeadBlock(blockHash);
        }

        public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlock)} calls");

        public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlockAsync)} calls");

        public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public AddBlockResult SuggestHeader(BlockHeader header) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestHeader)} calls");

        public Keccak HeadHash => _wrapped.HeadHash;
        public Keccak GenesisHash => _wrapped.GenesisHash;
        public Keccak PendingHash => _wrapped.PendingHash;
        public Keccak FinalizedHash => _wrapped.FinalizedHash;
        public Keccak SafeHash => _wrapped.SafeHash;

        public Block FindBlock(Keccak blockHash, BlockTreeLookupOptions options) => _wrapped.FindBlock(blockHash, options);

        public BlockHeader FindHeader(Keccak blockHash, BlockTreeLookupOptions options) => _wrapped.FindHeader(blockHash, options);

        public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) => _wrapped.FindHeader(blockNumber, options);
        public Keccak FindBlockHash(long blockNumber) => _wrapped.FindBlockHash(blockNumber);

        public bool IsMainChain(BlockHeader blockHeader) => _wrapped.IsMainChain(blockHeader);

        public Keccak FindHash(long blockNumber) => _wrapped.FindHash(blockNumber);

        public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse) => _wrapped.FindHeaders(hash, numberOfBlocks, skip, reverse);

        public BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth) => _wrapped.FindLowestCommonAncestor(firstDescendant, secondDescendant, maxSearchDepth);

        public Block FindBlock(long blockNumber, BlockTreeLookupOptions options) => _wrapped.FindBlock(blockNumber, options);

        public void DeleteInvalidBlock(Block invalidBlock) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(DeleteInvalidBlock)} calls");

        public bool IsMainChain(Keccak blockHash) => _wrapped.IsMainChain(blockHash);

        public BlockHeader FindBestSuggestedHeader() => _wrapped.FindBestSuggestedHeader();

        public bool IsKnownBlock(long number, Keccak blockHash) => _wrapped.IsKnownBlock(number, blockHash);

        public bool IsKnownBeaconBlock(long number, Keccak blockHash) => _wrapped.IsKnownBeaconBlock(number, blockHash);

        public bool WasProcessed(long number, Keccak blockHash) => _wrapped.WasProcessed(number, blockHash);

        public void LoadLowestInsertedBeaconHeader() => _wrapped.LoadLowestInsertedBeaconHeader();

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

        public int DeleteChainSlice(in long startNumber, long? endNumber = null)
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

                        IEnumerable<BlockHeader> GetPotentiallyCorruptedBlocks(long start)
                        {
                            for (long i = start; i <= endSearch; i++)
                            {
                                yield return _wrapped.FindHeader(i, BlockTreeLookupOptions.None);
                            }
                        }

                        if (GetPotentiallyCorruptedBlocks(startNumber).Any(b => b is null))
                        {
                            return _wrapped.DeleteChainSlice(startNumber);
                        }

                        throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} cannot {nameof(DeleteChainSlice)} if searched blocks [{startNumber}, {endSearch}] are not corrupted.");
                    }

                    throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} cannot {nameof(DeleteChainSlice)} if {nameof(startNumber)} is not past {nameof(Head)}.");
                }

                throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} cannot {nameof(DeleteChainSlice)} if {nameof(Head)} is not past Genesis.");
            }

            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(DeleteChainSlice)} calls with {nameof(endNumber)} other than {nameof(BestKnownNumber)} specified.");

        }

        public bool IsBetterThanHead(BlockHeader? header) => _wrapped.IsBetterThanHead(header);
        public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(UpdateBeaconMainChain)} calls");

        public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(UpdateMainChain)} calls");

        public void ForkChoiceUpdated(Keccak? finalizedBlockHash, Keccak? safeBlockBlockHash) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(ForkChoiceUpdated)} calls");
    }
}
