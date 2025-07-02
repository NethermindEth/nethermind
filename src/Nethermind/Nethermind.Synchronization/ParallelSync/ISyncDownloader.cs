// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync;

public interface ISyncDownloader<in T>
{
    public Task Dispatch(PeerInfo peerInfo, T request, CancellationToken cancellationToken);
}
