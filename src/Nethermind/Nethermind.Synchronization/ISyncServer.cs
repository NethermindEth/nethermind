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
        void HintBlock(Hash256 hash, long number, ISyncPeer receivedFrom);
        void AddNewBlock(Block block, ISyncPeer node);
        void StopNotifyingPeersAboutNewBlocks();
        /// <summary>
        /// Returns receipts for the given block hash, with explicit semantics:
        /// <list type="bullet">
        /// <item><description><c>null</c> — receipts are not (yet) known locally (block missing, body missing, or receipts not stored); callers MUST NOT claim "empty" on the wire.</description></item>
        /// <item><description>empty array — block exists locally and legitimately has zero transactions.</description></item>
        /// <item><description>non-empty array — receipts for an executed block.</description></item>
        /// </list>
        /// </summary>
        TxReceipt[]? GetReceipts(Hash256 blockHashes);
        MemoryManager<byte>? GetBlockAccessListRlp(Hash256 blockHash);
        Block? Find(Hash256 hash);
        BlockHeader? FindHeader(Hash256 hash);
        Hash256? FindHash(long number);
        IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse);
        IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        long LowestBlock { get; }
    }
}
