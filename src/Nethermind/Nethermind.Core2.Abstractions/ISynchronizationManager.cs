// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core2.P2p;

namespace Nethermind.Core2
{
    public interface ISynchronizationManager
    {
        Task OnPeerDialOutConnected(string peerId);
        Task OnStatusRequestReceived(string peerId, PeeringStatus peerPeeringStatus);
        Task OnStatusResponseReceived(string peerId, PeeringStatus peerPeeringStatus);
    }
}
