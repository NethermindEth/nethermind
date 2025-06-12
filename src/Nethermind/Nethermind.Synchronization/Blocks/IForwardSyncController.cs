// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

/// <summary>
/// This class act like a shared `ISyncFeed` for both fastsync and full sync.
/// </summary>
public interface IForwardSyncController
{
    public Task<BlocksRequest> PrepareRequest(DownloaderOptions buildOptions, int fastSyncLag, CancellationToken token);
    public SyncResponseHandlingResult HandleResponse(BlocksRequest response, PeerInfo? peer);

    public int DownloadRequestBufferSize { get; }
    public void PruneDownloadBuffer();
}
