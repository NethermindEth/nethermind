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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    // TODO: work in progress
    public class BlockTree : IBlockTree
    {
        private readonly Dictionary<Keccak, Block> _branches = new Dictionary<Keccak, Block>();
        private readonly Dictionary<Keccak, Block> _mainChain = new Dictionary<Keccak, Block>();
        private readonly Block[] _canonicalChain = new Block[10 * 1000 * 1000]; // 40MB

        public Keccak GenesisHash => FindBlock(0)?.Hash;
        public IChain MainChain { get; set; }

        public void AddBlock(Block block)
        {
            _branches.Add(block.Header.Hash, block);
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
            Block block = FindBlock(blockHash, true);
            if (block == null)
            {
                return result;
            }

            for (int i = 0; i < numberOfBlocks; i++)
            {
                result[i] = _canonicalChain[(int)block.Number + (reverse ? -1 : 1) * (skip + i)];
            }

            return result;
        }
        
        public Block FindBlock(BigInteger blockNumber)
        {
            if (blockNumber.Sign < 0)
            {
                throw new ArgumentException($"{nameof(blockNumber)} must be greater or equal zero and is {blockNumber}", nameof(blockNumber));
            }
            
            return _canonicalChain[(int)blockNumber];
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _mainChain.ContainsKey(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            Block block = _mainChain[blockHash];
            _canonicalChain[(int)block.Number] = null;
            
            _branches.Add(blockHash, block);
            _mainChain.Remove(blockHash);
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
            _mainChain.Add(blockHash, block);
            _branches.Remove(blockHash);
        }
    }
}