// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Trie;

public class GetNodeDataTrieNodeRecovery : TrieNodeRecovery<IReadOnlyList<Hash256>>
{
    public GetNodeDataTrieNodeRecovery(ISyncPeerPool syncPeerPool, ILogManager? logManager) : base(syncPeerPool, logManager)
    {
    }

    protected override string GetMissingNodes(IReadOnlyList<Hash256> request) => string.Join(", ", request);

    protected override bool CanAllocatePeer(ISyncPeer peer) => peer.CanGetNodeData();

    protected override async Task<byte[]?> RecoverRlpFromPeerBase(ValueHash256 rlpHash, ISyncPeer peer, IReadOnlyList<Hash256> request, CancellationTokenSource cts)
    {
        using IDisposableReadOnlyList<byte[]> rlp = await (peer.TryGetSatelliteProtocol(Protocol.NodeData, out INodeDataPeer nodeDataHandler)
            ? nodeDataHandler.GetNodeData(request, cts.Token)
            : peer.GetNodeData(request, cts.Token));

        if (rlp.Count() == 1)
        {
            byte[] recoveredRlp = rlp[0];
            if (ValueKeccak.Compute(recoveredRlp) == rlpHash)
            {
                return recoveredRlp;
            }

            if (_logger.IsDebug) _logger.Debug($"Recovered RLP from peer {peer} but the hash does not match");
        }

        return null;
    }
}
