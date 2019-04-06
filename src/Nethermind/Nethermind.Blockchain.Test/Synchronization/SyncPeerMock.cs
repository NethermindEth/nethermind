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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Test.Synchronization
{
    public class SyncPeerMock : ISyncPeer
    {
        private readonly IBlockTree _remoteTree;
        private readonly PublicKey _localPublicKey;
        private readonly ISyncServer _remoteSyncServer;

        public SyncPeerMock(IBlockTree remoteTree, PublicKey localPublicKey = null, string localClientId = "", ISyncServer remoteSyncServer = null, PublicKey remotePublicKey = null, string remoteClientId = "")
        {
            string localHost = "127.0.0.1";
            if (int.TryParse(localClientId.Replace("PEER", string.Empty), out int localIndex))
            {
                localHost = $"127.0.0.{localIndex}";    
            }
            
            string remoteHost = "127.0.0.1";
            if (int.TryParse(remoteClientId.Replace("PEER", string.Empty), out int remoteIndex))
            {
                remoteHost = $"127.0.0.{remoteIndex}";    
            }
            
            _remoteTree = remoteTree;
            _localPublicKey = localPublicKey;
            _remoteSyncServer = remoteSyncServer;
            Node = new Node(remotePublicKey ?? TestItem.PublicKeyA, remoteHost, 1234);
            LocalNode = new Node(localPublicKey ?? TestItem.PublicKeyB, localHost, 1235);
            Node.ClientId = remoteClientId;
            LocalNode.ClientId = localClientId;

            Task.Factory.StartNew(RunQueue, TaskCreationOptions.LongRunning);
        }

        private void RunQueue()
        {
            foreach (Action action in _sendQueue.GetConsumingEnumerable())
            {
                action();
            }
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsFastSyncSupported => false;
        public Node Node { get; }
        
        public Node LocalNode { get; }
        public string ClientId => Node.ClientId;
        public UInt256 TotalDifficultyOnSessionStart => _remoteTree.Head.TotalDifficulty ?? 0;

        public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
        {
            Block[] result = new Block[blockHashes.Length];
            for (int i = 0; i < blockHashes.Length; i++)
            {
                result[i] = _remoteTree.FindBlock(blockHashes[i], true);
            }
            
            return Task.FromResult(result);
        }

        public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            UInt256 firstNumber = _remoteTree.FindBlock(blockHash, true).Number;
            
            BlockHeader[] result = new BlockHeader[maxBlocks];
            for (int i = 0; i < maxBlocks; i++)
            {
                result[i] = _remoteTree.FindBlock((ulong)firstNumber + (ulong)i + (ulong)skip).Header;
            }
            
            return Task.FromResult(result);
        }
        
        public Task<BlockHeader[]> GetBlockHeaders(UInt256 number, int maxBlocks, int skip, CancellationToken token)
        {
            UInt256 firstNumber = _remoteTree.FindBlock(number).Number;
            
            BlockHeader[] result = new BlockHeader[maxBlocks];
            for (int i = 0; i < maxBlocks; i++)
            {
                ulong blockNumber = (ulong) firstNumber + (ulong) i + (ulong) skip;
                if (blockNumber > (_remoteTree.Head?.Number ?? 0))
                {
                    result[i] = null;
                }
                else
                {
                    result[i] = _remoteTree.FindBlock(blockNumber).Header;
                }
            }
            
            return Task.FromResult(result);
        }

        public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
        {
            return Task.FromResult(_remoteTree.Head);
        }

        private BlockingCollection<Action> _sendQueue = new BlockingCollection<Action>();
        
        public void SendNewBlock(Block block)
        {
            _sendQueue.Add(() => _remoteSyncServer?.AddNewBlock(block, LocalNode));
        }

        public void SendNewTransaction(Transaction transaction)
        {
        }

        public Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
        {
            return Task.FromResult(_remoteSyncServer.GetReceipts(blockHash));
        }

        public Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
        {
            return Task.FromResult(_remoteSyncServer.GetNodeData(hashes));
        }
    }
}