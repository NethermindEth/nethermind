// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Trie;

public class SnapTrieNodeRecovery : TrieNodeRecovery<GetTrieNodesRequest>
{
    public SnapTrieNodeRecovery(ISyncPeerPool syncPeerPool, ILogManager? logManager) : base(syncPeerPool, logManager)
    {
    }

    protected override bool CanAllocatePeer(ISyncPeer peer) => peer.CanGetSnapData();

    protected override async Task<byte[]?> RecoverRlpFromPeerBase(ISyncPeer peer, GetTrieNodesRequest request, CancellationTokenSource cts)
    {
        if (peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer? snapPeer))
        {
            byte[][] rlp = await snapPeer.GetTrieNodes(request, cts.Token);
            if (rlp.Length == 1)
            {
                byte[] recoveredRlp = rlp[0];
                if (_logger.IsWarn) _logger.Warn($"Recovered RLP from peer {peer} with {recoveredRlp.Length} bytes");
                return recoveredRlp;
            }
        }

        return null;
    }
}
