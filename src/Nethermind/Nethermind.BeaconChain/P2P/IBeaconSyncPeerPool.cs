// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P;

/// <summary>A connected, status-exchanged beacon chain peer usable by range sync.</summary>
public interface IBeaconSyncPeer
{
    string Id { get; }

    /// <summary>The head slot last advertised by the peer over <c>status</c>.</summary>
    ulong HeadSlot { get; }

    Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token);

    /// <summary>Records a protocol violation or failure; repeated reports get the peer pruned.</summary>
    void ReportFailure(string reason);
}

/// <summary>The pool of sync-usable peers maintained by the peer manager.</summary>
public interface IBeaconSyncPeerPool
{
    /// <summary>Returns peers advertising a head at or past <paramref name="minHeadSlot"/>, best head first.</summary>
    IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot);
}
