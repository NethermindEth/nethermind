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
