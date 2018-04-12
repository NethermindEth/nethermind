/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    // TODO: work in progress
    public class BlockTree : IBlockTree
    {
        private readonly ConcurrentDictionary<Keccak, Block> _branches = new ConcurrentDictionary<Keccak, Block>();
        private readonly ConcurrentDictionary<Keccak, Block> _mainChain = new ConcurrentDictionary<Keccak, Block>();
        private readonly ConcurrentDictionary<int, Block> _canonicalChain = new ConcurrentDictionary<int, Block>();

        public Keccak GenesisHash => FindBlock(0)?.Hash;
        public IChain MainChain { get; set; }

        public void AddBlock(Block block)
        {
            _branches.AddOrUpdate(block.Header.Hash, block, (h, b) =>
            {
                //https://stackoverflow.com/questions/17926519/concurrent-dictionary-addorupdate-vs-index-add?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
                Debug.Assert(block == b, "Assuming it would not happen, if never fired use indexer instead");
                return b;
            });
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            _mainChain.TryGetValue(blockHash, out Block block);
            if (block == null && !mainChainOnly)
            {
                _branches.TryGetValue(blockHash, out block);
            }

            return block;
        }

        public Block[] FindBlocks(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            Block[] result = new Block[numberOfBlocks];
            Block startBlock = FindBlock(blockHash, true);
            if (startBlock == null)
            {
                return result;
            }

            for (int i = 0; i < numberOfBlocks; i++)
            {
                _canonicalChain.TryGetValue((int)startBlock.Number + (reverse ? -1 : 1) * (skip + i), out Block ithBlock);
                result[i] = ithBlock;
            }

            return result;
        }

        public Block FindBlock(BigInteger blockNumber)
        {
            if (blockNumber.Sign < 0)
            {
                throw new ArgumentException($"{nameof(blockNumber)} must be greater or equal zero and is {blockNumber}", nameof(blockNumber));
            }

            _canonicalChain.TryGetValue((int)blockNumber, out Block block);
            return block;
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _mainChain.ContainsKey(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            Block block = _mainChain[blockHash];
            _canonicalChain[(int)block.Number] = null;

            _branches.AddOrUpdate(blockHash, block, (h, b) =>
            {
                Debug.Assert(block == b, "Assuming it would not happen, if never fired use indexer instead");
                return b;
            });

            if (!_mainChain.TryRemove(blockHash, out Block _))
            {
                throw new InvalidOperationException($"this should not happen as we should only be removing in {nameof(BlockchainProcessor)}");
            }
        }

        private readonly HashSet<Keccak> _processed = new HashSet<Keccak>();

        public void MarkAsProcessed(Keccak blockHash)
        {
            _processed.Add(blockHash);
        }

        public bool WasProcessed(Keccak blockHash)
        {
            return _processed.Contains(blockHash);
        }

        public void MoveToMain(Keccak blockHash)
        {
            Block block = _branches[blockHash];
            _canonicalChain[(int)block.Number] = block;

            _mainChain.AddOrUpdate(blockHash, block, (h, b) =>
            {
                Debug.Assert(block == b, "updating with a different block?");
                return b;
            });

            if (!_branches.TryRemove(blockHash, out Block _))
            {
                throw new InvalidOperationException($"this should not happen as we should only be removing in {nameof(BlockchainProcessor)}");
            }
        }
    }
}