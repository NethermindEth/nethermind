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

namespace Nethermind.Blockchain.Test.Synchronization.FastBlocks
{
    public class LatencySyncPeerMock : ISyncPeer
    {
        public IBlockTree Tree { get; }
        public bool IsReported { get; set; } = false;
        public long? BusyUntil { get; set; }
        public int Latency { get; set; }
        public static int RemoteIndex { get; set; } = 1;

        public LatencySyncPeerMock(IBlockTree tree, int latency = 5)
        {
            Latency = latency;
            string localHost = "0.0.0.0";

            string remoteHost = $"{RemoteIndex}.{RemoteIndex}.{RemoteIndex}.{RemoteIndex}";

            Tree = tree;
            Node = new Node(TestItem.PrivateKeys[RemoteIndex].PublicKey, remoteHost, 30303);
            LocalNode = new Node(TestItem.PrivateKeys[0].PublicKey, localHost, 30303);
            Node.ClientId = $"remote {RemoteIndex}";
            LocalNode.ClientId = "local nethermind";
            RemoteIndex++;
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        
        public bool IsFastSyncSupported => false;
        public Node Node { get; }
        public Node LocalNode { get; }
        public string ClientId => Node.ClientId;
        public UInt256 TotalDifficultyOnSessionStart => Tree.Head.TotalDifficulty ?? 0;

        public void Disconnect(DisconnectReason reason, string details)
        {
            throw new NotImplementedException();
        }

        public Task<BlockBody[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            throw new NotImplementedException();
        }
        
        public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
        {
            throw new NotImplementedException();
        }
        
        public void SendNewBlock(Block block)
        {
            throw new NotImplementedException();
        }

        public void SendNewTransaction(Transaction transaction)
        {
            throw new NotImplementedException();
        }

        public Task<TxReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }
}