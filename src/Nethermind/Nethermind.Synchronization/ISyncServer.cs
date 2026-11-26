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
        TxReceipt[]? GetReceipts(Hash256 blockHashes);
        /// <summary>Adds the receipts of the block with <paramref name="blockHash"/> as a receipts message carries them.</summary>
        /// <remarks>
        /// The receipts are the encoded sequence that <paramref name="decoder"/> writes for the receipts <see cref="GetReceipts"/> gives.
        /// The receipt finder copies the stored receipts without decoding them where it can give this form.
        /// </remarks>
        /// <param name="decoder">The decoder that encodes each receipt.</param>
        /// <param name="withBloom">The form that <paramref name="decoder"/> writes: with blooms for eth/63 to eth/68; otherwise the eth/69 form.</param>
        /// <param name="destination">The list that the encoded sequence of the block's receipts is added to.</param>
        /// <returns><see langword="false"/> when the block's receipts are not available; nothing is then added.</returns>
        bool TryAppendReceipts(Hash256 blockHash, IRlpDecoder<TxReceipt> decoder, bool withBloom, ArrayPoolList<byte> destination);
        /// <summary>Starts reading what <see cref="TryAppendReceipts"/> needs for <paramref name="count"/> hashes from <paramref name="start"/>, before the calls that need it.</summary>
        /// <remarks>Returns before the reads complete.</remarks>
        void PrefetchReceipts(IReadOnlyList<Hash256> hashes, int start, int count) { }
        MemoryManager<byte>? GetBlockAccessListRlp(Hash256 blockHash);
        /// <summary>The stored block with <paramref name="hash"/>, encoded as the block store holds it.</summary>
        /// <returns>The encoded block, which the caller disposes, or <see langword="null"/> when its body is not stored.</returns>
        MemoryManager<byte>? FindBlockRlp(Hash256 hash);
        /// <summary>Starts reading the stored blocks of <paramref name="count"/> hashes from <paramref name="start"/>, before the <see cref="FindBlockRlp"/> calls that need them.</summary>
        /// <remarks>Returns before the block reads complete.</remarks>
        void PrefetchBlocks(IReadOnlyList<Hash256> hashes, int start, int count) { }
        BlockHeader? FindHeader(Hash256 hash);
        Hash256? FindHash(ulong number);
        /// <summary>Finds the headers that a block headers request asks for, encoded for the network.</summary>
        /// <returns>The encoded headers one after another, up to the first header that is not found.</returns>
        IOwnedReadOnlyList<byte> FindHeadersRlp(Hash256 hash, int numberOfBlocks, int skip, bool reverse);
        IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        ulong LowestBlock { get; }
    }
}
