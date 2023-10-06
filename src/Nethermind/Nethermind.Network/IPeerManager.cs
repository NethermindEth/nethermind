// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;

namespace Nethermind.Network
{
    public interface IPeerManager
    {
        void Start();
        Task StopAsync();
        IReadOnlyCollection<Peer> ActivePeers { get; }
        IReadOnlyCollection<Peer> ConnectedPeers { get; }
        int MaxActivePeers { get; }
    }
}
