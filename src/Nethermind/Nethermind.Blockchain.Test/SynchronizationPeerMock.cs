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
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Blockchain.Test
{
    public class SynchronizationPeerMock : ISynchronizationPeer
    {
        private readonly IBlockTree _blockTree;

        public SynchronizationPeerMock(IBlockTree blockTree)
        {
            _blockTree = blockTree;
        }

        public PublicKey NodeId { get; set; } = TestObject.PublicKeyA;
        
        public Task<Block[]> GetBlocks(Keccak[] blockHashes)
        {
            Block[] result = new Block[blockHashes.Length];
            for (int i = 0; i < blockHashes.Length; i++)
            {
                result[i] = _blockTree.FindBlock(blockHashes[i], true);
            }
            
            return Task.FromResult(result);
        }

        public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip)
        {
            BigInteger firstNumber = _blockTree.FindBlock(blockHash, true).Number;
            
            BlockHeader[] result = new BlockHeader[maxBlocks];
            for (int i = 0; i < maxBlocks; i++)
            {
                result[i] = _blockTree.FindBlock((int)firstNumber + i + skip).Header;
            }
            
            return Task.FromResult(result);
        }
        
        public Task<BlockHeader[]> GetBlockHeaders(BigInteger number, int maxBlocks, int skip)
        {
            BigInteger firstNumber = _blockTree.FindBlock(number).Number;
            
            BlockHeader[] result = new BlockHeader[maxBlocks];
            for (int i = 0; i < maxBlocks; i++)
            {
                result[i] = _blockTree.FindBlock((int)firstNumber + i + skip).Header;
            }
            
            return Task.FromResult(result);
        }

        public Task<Keccak> GetHeadBlockHash()
        {
            return Task.FromResult(_blockTree.HeadBlock.Hash);
        }

        public Task<BigInteger> GetHeadBlockNumber()
        {
            return Task.FromResult(_blockTree.HeadBlock.Number);
        }

        public void SendNewBlock(Block block)
        {
            throw new NotImplementedException();
        }

        public void SendNewTransaction(Transaction transaction)
        {
            throw new NotImplementedException();
        }

        public Task Disconnect()
        {
            throw new NotImplementedException();
        }
    }
}