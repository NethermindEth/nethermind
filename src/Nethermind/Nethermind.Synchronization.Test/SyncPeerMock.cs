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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Test
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
            HeadNumber = _remoteTree.Head.Number;
            HeadHash = _remoteTree.Head.Hash;
            TotalDifficulty = _remoteTree.Head.TotalDifficulty ?? 0;
            
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

        public Node Node { get; }
        
        public Node LocalNode { get; }
        public string ClientId => Node.ClientId;
        public Keccak HeadHash { get; set; }
        public long HeadNumber { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public bool IsInitialized { get; set; }

        public void Disconnect(DisconnectReason reason, string details)
        {
        }

        public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
        {
            BlockBody[] result = new BlockBody[blockHashes.Count];
            for (int i = 0; i < blockHashes.Count; i++)
            {
                Block block = _remoteTree.FindBlock(blockHashes[i], BlockTreeLookupOptions.RequireCanonical);
                result[i] = new BlockBody(block.Transactions, block.Ommers);
            }
            
            return Task.FromResult(result);
        }

        public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            BlockHeader[] result = new BlockHeader[maxBlocks];
            long? firstNumber = _remoteTree.FindHeader(blockHash, BlockTreeLookupOptions.RequireCanonical)?.Number;
            if (!firstNumber.HasValue)
            {
                return Task.FromResult(result);
            }  
            
            for (int i = 0; i < maxBlocks; i++)
            {
                result[i] = _remoteTree.FindHeader(firstNumber.Value + i + skip, BlockTreeLookupOptions.RequireCanonical);
            }
            
            return Task.FromResult(result);
        }
        
        public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            BlockHeader[] result = new BlockHeader[maxBlocks];
            long? firstNumber = _remoteTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical)?.Number;
            if (!firstNumber.HasValue)
            {
                return Task.FromResult(result);
            }  
            
            for (int i = 0; i < maxBlocks; i++)
            {
                long blockNumber = firstNumber.Value + i + skip;
                if (blockNumber > (_remoteTree.Head?.Number ?? 0))
                {
                    result[i] = null;
                }
                else
                {
                    result[i] = _remoteTree.FindBlock(blockNumber, BlockTreeLookupOptions.None).Header;
                }
            }
            
            return Task.FromResult(result);
        }

        public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
        {
            return Task.FromResult(_remoteTree.Head?.Header);
        }

        private BlockingCollection<Action> _sendQueue = new BlockingCollection<Action>();
        
        public void NotifyOfNewBlock(Block block, SendBlockPriority priority)
        {
            if (priority == SendBlockPriority.High)
                SendNewBlock(block);
            else
                HintNewBlock(block.Hash, block.Number);
        }

        public void SendNewBlock(Block block)
        {
            _sendQueue.Add(() => _remoteSyncServer?.AddNewBlock(block, this));
        }

        public void HintNewBlock(Keccak blockHash, long number)
        {
            _sendQueue.Add(() => _remoteSyncServer?.HintBlock(blockHash, number, this));
        }

        public PublicKey Id => Node.Id;

        public bool SendNewTransaction(Transaction transaction, bool isPriority) => true;

        public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
        {
            TxReceipt[][] result = new TxReceipt[blockHash.Count][];
            for (int i = 0; i < blockHash.Count; i++)
            {
                result[i] = _remoteSyncServer.GetReceipts(blockHash[i]);
            }
            
            return Task.FromResult(result);
        }

        public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
        {
            return Task.FromResult(_remoteSyncServer.GetNodeData(hashes));
        }

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }
    }
}
