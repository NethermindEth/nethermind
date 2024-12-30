// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Synchronization.ParallelSync;
internal class PortalSyncFeed : ActivatedSyncFeed<PortalBatch>
{
    public override bool IsMultiFeed => true;

    public override AllocationContexts Contexts => AllocationContexts.All;

    public override bool IsFinished => false;

    protected override SyncMode ActivationSyncModes => SyncMode.PortalSync;

    public override SyncResponseHandlingResult HandleResponse(PortalBatch response, PeerInfo peer = null)
    {
        throw new NotImplementedException();
    }

    public override Task<PortalBatch> PrepareRequest(CancellationToken token = default)
    {
        throw new NotImplementedException();
    }
}

class PortalBatch
{
    //public BlockInfo?[] Infos { get; } = infos;
    //public OwnedBlockBodies? Response { get; set; }
    //public override long? MinNumber => Infos[0].BlockNumber;

    //public override void Dispose()
    //{
    //    base.Dispose();
    //    Response?.Dispose();
    //}

}
