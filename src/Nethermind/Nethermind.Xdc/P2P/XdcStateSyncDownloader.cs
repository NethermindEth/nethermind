// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Xdc;

internal class XdcStateSyncDownloader(ILogManager logManager) : StateSyncDownloader(logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<XdcStateSyncDownloader>();

    protected override bool ProtocolSupportsNodeData(ISyncPeer peer) => peer.ProtocolVersion < 101;

    public override async Task Dispatch(PeerInfo peerInfo, StateSyncBatch batch, CancellationToken cancellationToken)
    {
        if (_logger.IsInfo)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            bool hasNodeDataProtocol = peer.TryGetSatelliteProtocol<object>(Protocol.NodeData, out _);
            bool supportsNodeData = ProtocolSupportsNodeData(peer);
            bool hasSnap = peer.TryGetSatelliteProtocol<object>(Protocol.Snap, out _);
            _logger.Info($"XDC state sync dispatch: peer={peer}, version={peer.ProtocolVersion}, hasNodeDataProtocol={hasNodeDataProtocol}, supportsGetNodeData={supportsNodeData}, hasSnap={hasSnap}, batchSize={batch.RequestedNodes?.Count ?? 0}");
        }

        await base.Dispatch(peerInfo, batch, cancellationToken);

        if (_logger.IsInfo)
        {
            int responseCount = batch.Responses?.Count ?? 0;
            int requestCount = batch.RequestedNodes?.Count ?? 0;
            _logger.Info($"XDC state sync response: peer={peerInfo.SyncPeer}, responses={responseCount}/{requestCount}");
        }
    }
}
