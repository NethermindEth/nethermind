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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain
{
    public interface ISynchronizationManager
    {
        void HintBlock(Keccak hash, UInt256 number, PublicKey receivedFrom);
        byte[][] GetNodeData(Keccak[] keys);
        TransactionReceipt[][] GetReceipts(Keccak[] blockHashes);
        Block Find(Keccak hash);
        Block Find(UInt256 number);
        Block[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse);
        void AddNewBlock(Block block, PublicKey nodeWhoSentTheBlock);
        void AddPeer(ISynchronizationPeer syncPeer);
        void RemovePeer(ISynchronizationPeer syncPeer);
        int GetPeerCount();
        void Start();
        Task StopAsync();
        
        int ChainId { get; }
        BlockHeader Genesis { get; }
        BlockHeader Head { get; }
        UInt256 HeadNumber { get; }
        UInt256 TotalDifficulty { get; }

        event EventHandler<SyncEventArgs> SyncEvent;
    }
}