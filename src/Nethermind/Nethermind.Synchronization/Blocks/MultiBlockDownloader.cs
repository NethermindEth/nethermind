// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        if (request.AllCounts == 0)
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
        ReadOnlySpan<BlockHeader> headersSpan = headers.AsSpan();
        ArrayPoolList<Hash256> hashes = new(headersSpan.Length);
        for (int i = 0; i < headersSpan.Length; i++)
        {
            hashes.Add(headersSpan[i].Hash!);
        }

        return hashes;
    }
}
