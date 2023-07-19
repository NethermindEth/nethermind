// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
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

    protected override string GetMissingNodes(GetTrieNodesRequest request) =>
        string.Join("; ", request.AccountAndStoragePaths.Select(GetMissingNodes));

    private string GetMissingNodes(PathGroup requestAccountAndStoragePaths) =>
        requestAccountAndStoragePaths.Group.Length switch
        {
            1 => $"Account: {requestAccountAndStoragePaths.Group[0].ToHexString()}",
            > 1 => $"Account: {requestAccountAndStoragePaths.Group[0].ToHexString()}, Storage: {string.Join(", ", requestAccountAndStoragePaths.Group.Skip(1).Select(g => g.ToHexString()))}",
            _ => "",
        };

    protected override bool CanAllocatePeer(ISyncPeer peer) => peer.CanGetSnapData();

    protected override async Task<byte[]?> RecoverRlpFromPeerBase(ISyncPeer peer, GetTrieNodesRequest request, CancellationTokenSource cts)
    {
        if (peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer? snapPeer))
        {
            byte[][] rlp = await snapPeer.GetTrieNodes(request, cts.Token);
            if (rlp.Length == 1 && rlp[0]?.Length > 0)
            {
                // TODO: How to verify we got correct RLP?
                return rlp[0];
            }
        }

        return null;
    }
}
