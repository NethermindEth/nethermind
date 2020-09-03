//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Threading.Tasks;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface INetworkPeering
    {
        Slot HighestPeerSlot { get; }
        Slot SyncStartingSlot { get; }

        Task DisconnectPeerAsync(string peerId);

        // TODO: Should have CancellationToken, but Mothra won't support it, so add if/when we do a managed implementation
        Task PublishAttestationAsync(Attestation signedAttestation);
        Task PublishBeaconBlockAsync(SignedBeaconBlock signedBlock);
        Task RequestBlocksAsync(string peerId, Root peerHeadRoot, Slot finalizedSlot, Slot peerHeadSlot);
        Task SendBlockAsync(string peerId, SignedBeaconBlock signedBlock);
        Task SendStatusAsync(string peerId, RpcDirection rpcDirection, PeeringStatus peeringStatus);
    }
}