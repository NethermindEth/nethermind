// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.LesSync;

namespace Nethermind.Synchronization
{
    public interface ISyncServer : IDisposable
    {
        void HintBlock(Commitment hash, long number, ISyncPeer receivedFrom);
        void AddNewBlock(Block block, ISyncPeer node);
        void StopNotifyingPeersAboutNewBlocks();
        TxReceipt[] GetReceipts(Commitment blockHashes);
        Block? Find(Commitment hash);
        BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant);
        public Task BuildCHT();
        public CanonicalHashTrie? GetCHT();
        Commitment? FindHash(long number);
        BlockHeader[] FindHeaders(Commitment hash, int numberOfBlocks, int skip, bool reverse);
        byte[]?[] GetNodeData(IReadOnlyList<Commitment> keys, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        Commitment[]? GetBlockWitnessHashes(Commitment blockHash);
    }
}
