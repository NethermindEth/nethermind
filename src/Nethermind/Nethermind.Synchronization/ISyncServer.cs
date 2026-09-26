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
        BlockHeader? FindHeader(Hash256 hash);
        Hash256? FindHash(ulong number);
        IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse);
        /// <summary>Serves contract code by hash; state trie nodes are not served by hash.</summary>
        IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        ulong LowestBlock { get; }
    }
}
