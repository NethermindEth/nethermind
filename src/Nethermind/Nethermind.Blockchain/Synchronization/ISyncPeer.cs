// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISyncPeer : ITxPoolPeer, IPeerWithSatelliteProtocol
    {
        Node Node { get; }

        string Name { get; }
        string ClientId => Node?.ClientId;
        NodeClientType ClientType => Node?.ClientType ?? NodeClientType.Unknown;
        Hash256 HeadHash { get; set; }

        /// <summary>
        /// Total difficulty of the peer.
        /// <c>null</c> means peer is available post-merge only and does not support/provide TD.
        /// </summary>
        UInt256? TotalDifficulty { get; set; }

        bool IsInitialized { get; set; }
        bool IsPriority { get; set; }
        byte ProtocolVersion { get; }
        string ProtocolCode { get; }

        void Disconnect(DisconnectReason reason, string details);
        Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token);
        Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token);
        Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token);
        Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token);
        void NotifyOfNewBlock(Block block, SendBlockMode mode);
        void NotifyOfNewRange(BlockHeader earliest, BlockHeader latest) { }
        Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token);
        Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token);
    }
}
