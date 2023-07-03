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
        Task<Keccak[]> GetBlockWitnessHashes(Keccak blockHash, CancellationToken token);
    }

    public interface ISyncPeer : ITxPoolPeer, IPeerWithSatelliteProtocol
    {
        Node Node { get; }

        string Name { get; }
        string ClientId => Node?.ClientId;
        NodeClientType ClientType => Node?.ClientType ?? NodeClientType.Unknown;
        Keccak HeadHash { get; set; }
        long HeadNumber { get; set; }
        UInt256 TotalDifficulty { get; set; }
        bool IsInitialized { get; set; }
        bool IsPriority { get; set; }
        byte ProtocolVersion { get; }
        string ProtocolCode { get; }
        void Disconnect(InitiateDisconnectReason reason, string details);
        Task<BlockBody[]> GetBlockBodies(IReadOnlyList<Keccak> blockHashes, CancellationToken token);
        Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token);
        Task<BlockHeader[]> GetBlockHeaders(Keccak startHash, int maxBlocks, int skip, CancellationToken token);
        Task<BlockHeader?> GetHeadBlockHeader(Keccak? hash, CancellationToken token);
        void NotifyOfNewBlock(Block block, SendBlockMode mode);
        Task<TxReceipt[]?[]> GetReceipts(IReadOnlyList<Keccak> blockHash, CancellationToken token);
        Task<byte[][]> GetNodeData(IReadOnlyList<Keccak> hashes, CancellationToken token);
    }
}
