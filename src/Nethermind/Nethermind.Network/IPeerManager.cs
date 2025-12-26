// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Network
{
    public interface IPeerManager : IStoppableService
    {
        void Start();
        IReadOnlyCollection<Peer> ActivePeers { get; }
        IReadOnlyCollection<Peer> ConnectedPeers { get; }
        int MaxActivePeers { get; }
        int ActivePeersCount { get; }
    }
}
