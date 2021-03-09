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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISyncPeer : ITxPoolPeer, IPeerWithSatelliteProtocol
    {
        Node Node { get; }

        string ClientId => Node?.ClientId;
        NodeClientType ClientType => Node?.ClientType ?? NodeClientType.Unknown;
        Keccak HeadHash { get; set; }
        long HeadNumber { get; set; }
        UInt256 TotalDifficulty { get; set; }
        bool IsInitialized { get; set; }
        void Disconnect(DisconnectReason reason, string details);
        Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token);
        Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token);
        Task<BlockHeader?> GetHeadBlockHeader(Keccak hash, CancellationToken token);
        void NotifyOfNewBlock(Block block, SendBlockPriority priority);
        Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token);
        Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token);
    }
}
