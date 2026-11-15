// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.Synchronization
{
    public interface ISyncServer : IDisposable
    {
        void HintBlock(Hash256 hash, ulong number, ISyncPeer receivedFrom);
        void AddNewBlock(Block block, ISyncPeer node);
        void StopNotifyingPeersAboutNewBlocks();
        TxReceipt[]? GetReceipts(Hash256 blockHashes);
        /// <summary>Adds the receipts of the block with <paramref name="blockHash"/> as a receipts message carries them.</summary>
        /// <remarks>
        /// The receipts are the encoded sequence that <paramref name="decoder"/> writes for the receipts <see cref="GetReceipts"/> gives.
        /// When <paramref name="copyStored"/> is set, the stored receipts are copied without decoding them where their form allows it.
        /// </remarks>
        /// <param name="decoder">The decoder that encodes each receipt.</param>
        /// <param name="copyStored">
        /// Whether <paramref name="decoder"/> writes the form that the stored receipts can be copied in, which <paramref name="withBloom"/> selects.
        /// </param>
        /// <param name="withBloom">The form the stored receipts are copied in: with blooms for eth/63 to eth/68; otherwise the eth/69 form.</param>
        /// <param name="destination">The list that the encoded sequence of the block's receipts is added to.</param>
        /// <returns><see langword="false"/> when the block's receipts are not available; nothing is then added.</returns>
        bool TryAppendReceipts(Hash256 blockHash, IRlpDecoder<TxReceipt> decoder, bool copyStored, bool withBloom, ArrayPoolList<byte> destination);
        MemoryManager<byte>? GetBlockAccessListRlp(Hash256 blockHash);
        /// <summary>The stored block with <paramref name="hash"/>, encoded as the block store holds it.</summary>
        /// <returns>The encoded block, which the caller disposes, or <see langword="null"/> when its body is not stored.</returns>
        MemoryManager<byte>? FindBlockRlp(Hash256 hash);
        BlockHeader? FindHeader(Hash256 hash);
        Hash256? FindHash(ulong number);
        IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse);
        IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        ulong LowestBlock { get; }
    }
}
