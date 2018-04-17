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

using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public interface ISynchronizationManager
    {
        void HintBlock(Keccak hash, BigInteger number, PublicKey receivedFrom);
        Block Find(Keccak hash);
        Block Find(BigInteger number);
        Block[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse);
        void AddNewBlock(Block block, PublicKey receivedFrom);
        void AddNewTransaction(Transaction transaction, PublicKey receivedFrom);
        Task AddPeer(ISynchronizationPeer synchronizationPeer);
        void RemovePeer(ISynchronizationPeer synchronizationPeer);
        void Start();
        Task StopAsync();
        
        int ChainId { get; }
        Block GenesisBlock { get; }
        Block HeadBlock { get; }
        BigInteger HeadNumber { get; }
        BigInteger TotalDifficulty { get; }
        
        IBlockTree BlockTree { get; set; }
    }
}