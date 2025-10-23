// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        TxReceipt[] GetReceipts(Hash256 blockHashes);
        Block? Find(Hash256 hash);
        Hash256? FindHash(long number);
        IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse);
        IOwnedReadOnlyList<byte[]?> GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        long LowestBlock { get; }
    }
}
