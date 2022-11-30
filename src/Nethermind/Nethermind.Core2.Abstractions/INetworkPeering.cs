// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
