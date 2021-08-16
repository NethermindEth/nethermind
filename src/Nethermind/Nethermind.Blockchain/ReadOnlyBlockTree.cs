//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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

        public ulong ChainId => _wrapped.ChainId;
        public BlockHeader Genesis => _wrapped.Genesis;
        public BlockHeader BestSuggestedHeader => _wrapped.BestSuggestedHeader;
        public BlockHeader LowestInsertedHeader => _wrapped.LowestInsertedHeader;

        public long? LowestInsertedBodyNumber
        {
          get => _wrapped.LowestInsertedBodyNumber;
          set => _wrapped.LowestInsertedBodyNumber = value;
        }
        
        public Block BestSuggestedBody => _wrapped.BestSuggestedBody;
        public long BestKnownNumber => _wrapped.BestKnownNumber;
        public Block Head => _wrapped.Head;
        public bool CanAcceptNewBlocks { get; } = false;

        public async Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
        {
            await _wrapped.Accept(blockTreeVisitor, cancellationToken);
        }

        public ChainLevelInfo FindLevel(long number) => _wrapped.FindLevel(number);
        public BlockInfo FindCanonicalBlockInfo(long blockNumber) => _wrapped.FindCanonicalBlockInfo(blockNumber);

        public AddBlockResult Insert(Block block) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public void Insert(IEnumerable<Block> blocks) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");
        public void UpdateHeadBlock(Keccak blockHash)
        {
            // hacky while there is not special tree for RPC 
            _wrapped.UpdateHeadBlock(blockHash);
        }

        public AddBlockResult SuggestBlock(Block block, bool shouldProcess = true, bool? setAsMain = null) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlock)} calls");

        public AddBlockResult Insert(BlockHeader header) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");

        public AddBlockResult SuggestHeader(BlockHeader header) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestHeader)} calls");

        public Keccak HeadHash => _wrapped.HeadHash;
        public Keccak GenesisHash => _wrapped.GenesisHash;
        public Keccak PendingHash => _wrapped.PendingHash;

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

        public bool WasProcessed(long number, Keccak blockHash) => _wrapped.WasProcessed(number, blockHash);

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

        public int DeleteChainSlice(in long startNumber, long? endNumber = null)
        {
            var bestKnownNumber = BestKnownNumber;
            if (endNumber == null || endNumber == bestKnownNumber)
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
                        
                        if (GetPotentiallyCorruptedBlocks(startNumber).Any(b => b == null))
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

        public void UpdateMainChain(Block[] blocks, bool wereProcessed, bool forceHeadBlock = false) => throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(UpdateMainChain)} calls");
    }
}
