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
        MemoryManager<byte>? GetBlockAccessListRlp(Hash256 blockHash);
        Block? Find(Hash256 hash);

        /// <summary>Appends the body of the block with <paramref name="hash"/> to <paramref name="destination"/> as one network block body item when its transactions payload is at most <paramref name="maxTransactionsLength"/> bytes.</summary>
        /// <param name="transactionsLength">The payload length of the transactions sequence of the body.</param>
        /// <returns>Whether the block was found.</returns>
        bool TryCopyBlockBodyTo(Hash256 hash, ArrayPoolList<byte> destination, int maxTransactionsLength, out int transactionsLength)
        {
            Block? block = Find(hash);
            transactionsLength = block is null ? 0 : BlockBodyDecoder.Instance.EncodeTo(destination, block.Body, maxTransactionsLength);
            return block is not null;
        }

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
