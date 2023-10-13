// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public interface IWitnessPeer
    {
        Task<Commitment[]> GetBlockWitnessHashes(Commitment blockHash, CancellationToken token);
    }

    public interface ISyncPeer : ITxPoolPeer, IPeerWithSatelliteProtocol
    {
        Node Node { get; }

        string Name { get; }
        string ClientId => Node?.ClientId;
        NodeClientType ClientType => Node?.ClientType ?? NodeClientType.Unknown;
        Commitment HeadHash { get; set; }
        long HeadNumber { get; set; }
        UInt256 TotalDifficulty { get; set; }
        bool IsInitialized { get; set; }
        bool IsPriority { get; set; }
        byte ProtocolVersion { get; }
        string ProtocolCode { get; }
        void Disconnect(DisconnectReason reason, string details);
        Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Commitment> blockHashes, CancellationToken token);
        Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token);
        Task<BlockHeader[]> GetBlockHeaders(Commitment startHash, int maxBlocks, int skip, CancellationToken token);
        Task<BlockHeader?> GetHeadBlockHeader(Commitment? hash, CancellationToken token);
        void NotifyOfNewBlock(Block block, SendBlockMode mode);
        Task<TxReceipt[]?[]> GetReceipts(IReadOnlyList<Commitment> blockHash, CancellationToken token);
        Task<byte[][]> GetNodeData(IReadOnlyList<Commitment> hashes, CancellationToken token);
    }
}
