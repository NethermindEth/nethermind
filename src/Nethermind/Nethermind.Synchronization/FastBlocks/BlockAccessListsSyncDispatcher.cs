// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastBlocks;

public class BlockAccessListsSyncDispatcher : ISyncDownloader<BlockAccessListsSyncBatch>
{
    private readonly ILogger Logger;

    public BlockAccessListsSyncDispatcher(ILogManager logManager)
    {
        Logger = logManager.GetClassLogger();
    }

    public async Task Dispatch(PeerInfo peerInfo, BlockAccessListsSyncBatch batch, CancellationToken cancellationToken)
    {
        ISyncPeer peer = peerInfo.SyncPeer;
        batch.ResponseSourcePeer = peerInfo;
        batch.MarkSent();

        using ArrayPoolList<Hash256> hashes = new(batch.Infos.Length);
        for (int i = 0; i < batch.Infos.Length; i++)
        {
            if (batch.Infos[i] is not null)
            {
                hashes.Add(batch.Infos[i]!.BlockHash);
            }
        }

        if (hashes.Count == 0)
        {
            if (Logger.IsDebug) Logger.Debug($"{batch} - attempted send a request with no hash.");
            return;
        }

        try
        {
            batch.Response = await peer.GetBlockAccessLists(hashes, cancellationToken);
        }
        catch (TimeoutException)
        {
            if (Logger.IsDebug) Logger.Debug($"{batch} - request access lists timeout {batch.RequestTime:F2}");
            return;
        }

        if (batch.RequestTime > 1000)
        {
            if (Logger.IsDebug) Logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
        }
    }
}
