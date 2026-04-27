// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public class MultiBlockDownloader : ISyncDownloader<BlocksRequest>
{
    public async Task Dispatch(PeerInfo peerInfo, BlocksRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.BodiesRequests.Count == 0 && request.BlockAccessListsRequests.Count == 0 && request.ReceiptsRequests.Count == 0)
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
            using ArrayPoolList<Hash256> bodiesHash = BuildHashList(request.BodiesRequests);
            request.OwnedBodies = await peerInfo.SyncPeer.GetBlockBodies(bodiesHash, cancellationToken);
        }

        if (request.BlockAccessListsRequests.Count > 0 && peerInfo.SyncPeer.SupportsBlockAccessLists())
        {
            using ArrayPoolList<Hash256> blockAccessListsHash = BuildHashList(request.BlockAccessListsRequests);
            request.BlockAccessLists = await peerInfo.SyncPeer.GetBlockAccessLists(blockAccessListsHash, cancellationToken);
        }

        if (request.ReceiptsRequests.Count > 0)
        {
            using ArrayPoolList<Hash256> receiptsHash = BuildHashList(request.ReceiptsRequests);
            IOwnedReadOnlyList<TxReceipt[]?> ownedReceipts = await peerInfo.SyncPeer.GetReceipts(receiptsHash, cancellationToken);
            request.Receipts = ownedReceipts;
        }
    }

    private static ArrayPoolList<Hash256> BuildHashList(IOwnedReadOnlyList<BlockHeader> headers)
    {
        ArrayPoolList<Hash256> hashes = new(headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            hashes.Add(headers[i].Hash!);
        }

        return hashes;
    }
}
