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
        void HintBlock(Keccak hash, long number, ISyncPeer receivedFrom);
        void AddNewBlock(Block block, ISyncPeer node);
        void StopNotifyingPeersAboutNewBlocks();
        TxReceipt[] GetReceipts(Keccak blockHashes);
        Block? Find(Keccak hash);
        BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant);
        public Task BuildCHT();
        public CanonicalHashTrie? GetCHT();
        Keccak? FindHash(long number);
        BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse);
        byte[]?[] GetNodeData(IReadOnlyList<Keccak> keys, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State);
        int GetPeerCount();
        ulong NetworkId { get; }
        BlockHeader Genesis { get; }
        BlockHeader? Head { get; }
        Keccak[]? GetBlockWitnessHashes(Keccak blockHash);
    }
}
