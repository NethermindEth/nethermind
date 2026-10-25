// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        string? ClientId => Node.ClientId;
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
        Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong number, int maxBlocks, int skip, CancellationToken token);
        Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token);
        /// <summary>
        /// Asks the peer for the header of <paramref name="hash"/>, or of its announced head when null.
        /// </summary>
        /// <returns>
        /// The header of the requested block, or <c>null</c> if the peer does not have it. A peer answering with
        /// any other block is disconnected, so callers never need to re-check the hash of the result.
        /// </returns>
        Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token);
        void NotifyOfNewBlock(Block block, SendBlockMode mode);
        void NotifyOfNewRange(BlockHeader earliest, BlockHeader latest) { }
        Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token);

        /// <summary>
        /// Asks the peer for the receipts of <paramref name="blockHashes"/>, rejecting a response that holds more receipts
        /// for a block than <paramref name="expectedReceiptCounts"/> allows.
        /// </summary>
        /// <param name="blockHashes">The blocks to get the receipts of.</param>
        /// <param name="expectedReceiptCounts">
        /// Per block in <paramref name="blockHashes"/>, its transaction count, or a negative value when unknown;
        /// blocks past its end have no known count. It must stay unchanged until the returned task completes.
        /// </param>
        /// <param name="token">Cancels the request.</param>
        Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHashes, ReadOnlyMemory<int> expectedReceiptCounts, CancellationToken token) =>
            GetReceipts(blockHashes, token);
        Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token);
        Task<IOwnedReadOnlyList<byte[]?>> GetBlockAccessLists(IReadOnlyList<Hash256> blockHashes, CancellationToken token) =>
            Task.FromResult(IOwnedReadOnlyList<byte[]?>.Empty);
    }
}
