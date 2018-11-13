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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats;

namespace Nethermind.Blockchain.Test
{
    public class SynchronizationPeerMock : ISynchronizationPeer
    {
        private readonly IBlockTree _blockTree;

        public SynchronizationPeerMock(IBlockTree blockTree, PublicKey publicKey = null)
        {
            _blockTree = blockTree;
            NodeId = new NodeId(publicKey ?? TestObject.PublicKeyA);
        }

        public bool IsFastSyncSupported => false;
        public NodeId NodeId { get; set; }
        public INodeStats NodeStats { get; set; }
        public string ClientId { get; set; }

        public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
        {
            Block[] result = new Block[blockHashes.Length];
            for (int i = 0; i < blockHashes.Length; i++)
            {
                result[i] = _blockTree.FindBlock(blockHashes[i], true);
            }
            
            return Task.FromResult(result);
        }

        public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            UInt256 firstNumber = _blockTree.FindBlock(blockHash, true).Number;
            
            BlockHeader[] result = new BlockHeader[maxBlocks];
            for (int i = 0; i < maxBlocks; i++)
            {
                result[i] = _blockTree.FindBlock((ulong)firstNumber + (ulong)i + (ulong)skip).Header;
            }
            
            return Task.FromResult(result);
        }
        
        public Task<BlockHeader[]> GetBlockHeaders(UInt256 number, int maxBlocks, int skip, CancellationToken token)
        {
            UInt256 firstNumber = _blockTree.FindBlock(number).Number;
            
            BlockHeader[] result = new BlockHeader[maxBlocks];
            for (int i = 0; i < maxBlocks; i++)
            {
                ulong blockNumber = (ulong) firstNumber + (ulong) i + (ulong) skip;
                if (blockNumber > (_blockTree.Head?.Number ?? 0))
                {
                    result[i] = null;
                }
                else
                {
                    result[i] = _blockTree.FindBlock(blockNumber).Header;
                }
            }
            
            return Task.FromResult(result);
        }

        public Task<Keccak> GetHeadBlockHash(CancellationToken token)
        {
            return Task.FromResult(_blockTree.Head.Hash);
        }

        public Task<UInt256> GetHeadBlockNumber(CancellationToken token)
        {
            return Task.FromResult(_blockTree.Head.Number);
        }

        public Task<UInt256> GetHeadDifficulty(CancellationToken token)
        {
            return Task.FromResult(_blockTree.Head.Difficulty);
        }

        public void SendNewBlock(Block block)
        {
            throw new NotImplementedException();
        }

        public void SendNewTransaction(Transaction transaction)
        {
        }

        public Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash)
        {
            throw new NotImplementedException();
        }

        public void SendReceipts(TransactionReceipt[][] receipts)
        {
            throw new NotImplementedException();
        }

        public Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<byte[][]> GetNodeData(Keccak[] hashes)
        {
            throw new NotImplementedException();
        }

        public void SendNodeData(byte[][] values)
        {
            throw new NotImplementedException();
        }

        public Task Disconnect()
        {
            throw new NotImplementedException();
        }
    }
}