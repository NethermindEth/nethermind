// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public class MultiBlockDownloader : ISyncDownloader<BlocksRequest>
{
    public async Task Dispatch(PeerInfo peerInfo, BlocksRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.BodiesRequests.Count == 0 && request.ReceiptsRequests.Count == 0)
        {
            request.DownloadTask = Task.CompletedTask;
            return;
        }

        request.DownloadTask = DownloadAsync(peerInfo, request, cancellationToken);
        await request.DownloadTask;
    }

    private static async Task DownloadAsync(PeerInfo peerInfo, BlocksRequest request, CancellationToken cancellationToken)
    {
        if (request.BodiesRequests.Count > 0)
        {
            using IOwnedReadOnlyList<Hash256> bodiesHash = request.BodiesRequests.Select(b => b.Hash)
                .ToPooledList(request.BodiesRequests.Count);
            request.OwnedBodies = await peerInfo.SyncPeer.GetBlockBodies(bodiesHash, cancellationToken);
        }

        if (request.ReceiptsRequests.Count > 0)
        {
            using IOwnedReadOnlyList<Hash256> receiptsHash = request.ReceiptsRequests.Select(b => b.Hash)
                .ToPooledList(request.ReceiptsRequests.Count);
            var ownedReceipts = await peerInfo.SyncPeer.GetReceipts(receiptsHash, cancellationToken);
            request.Receipts = ownedReceipts;
        }
    }
}
