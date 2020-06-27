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
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class SynchronizationManager : ISynchronizationManager
    {
        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly IForkChoice _forkChoice;
        private readonly ILogger _logger;
        private readonly INetworkPeering _networkPeering;
        private readonly IStore _store;

        public SynchronizationManager(
            ILogger<SynchronizationManager> logger,
            IBeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            IForkChoice forkChoice,
            IStore store,
            INetworkPeering networkPeering)
        {
            _logger = logger;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _forkChoice = forkChoice;
            _store = store;
            _networkPeering = networkPeering;
        }

        public async Task OnPeerDialOutConnected(string peerId)
        {
            Root headRoot = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconState beaconState = await _store.GetBlockStateAsync(headRoot).ConfigureAwait(false);

            // Send request
            var status = BuildStatusFromHead(headRoot, beaconState);
            if (_logger.IsDebug()) LogDebug.SendingStatusToPeer(_logger, RpcDirection.Request, status, peerId, null);
            await _networkPeering.SendStatusAsync(peerId, RpcDirection.Request, status).ConfigureAwait(false);
        }

        public async Task OnStatusRequestReceived(string peerId, PeeringStatus peerPeeringStatus)
        {
            Root headRoot = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconState beaconState = await _store.GetBlockStateAsync(headRoot).ConfigureAwait(false);

            // Send response
            var status = BuildStatusFromHead(headRoot, beaconState);
            if (_logger.IsDebug()) LogDebug.SendingStatusToPeer(_logger, RpcDirection.Response, status, peerId, null);
            await _networkPeering.SendStatusAsync(peerId, RpcDirection.Response, status).ConfigureAwait(false);

            // Determine if the peer is valid, and if we need to request blocks
            await HandlePeerStatus(peerId, peerPeeringStatus, headRoot, beaconState);
        }

        public async Task OnStatusResponseReceived(string peerId, PeeringStatus peerPeeringStatus)
        {
            Root headRoot = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconState beaconState = await _store.GetBlockStateAsync(headRoot).ConfigureAwait(false);

            // Determine if the peer is valid, and if we need to request blocks
            await HandlePeerStatus(peerId, peerPeeringStatus, headRoot, beaconState);
        }

        private PeeringStatus BuildStatusFromHead(Root headRoot, BeaconState beaconState)
        {
            Epoch headEpoch = _beaconStateAccessor.GetCurrentEpoch(beaconState);
            ForkVersion headForkVersion;
            if (headEpoch < beaconState.Fork.Epoch)
            {
                headForkVersion = beaconState.Fork.PreviousVersion;
            }
            else
            {
                headForkVersion = beaconState.Fork.CurrentVersion;
            }

            PeeringStatus peeringStatus = new PeeringStatus(headForkVersion, beaconState.FinalizedCheckpoint.Root,
                beaconState.FinalizedCheckpoint.Epoch, headRoot, beaconState.Slot);
            return peeringStatus;
        }

        private async Task HandlePeerStatus(string peerId, PeeringStatus peerPeeringStatus, Root headRoot,
            BeaconState beaconState)
        {
            // if not valid, "immediately disconnect from one another following the handshake"
            bool isValidPeer = await IsValidPeerStatus(peerId, peerPeeringStatus, headRoot, beaconState)
                .ConfigureAwait(false);
            if (!isValidPeer)
            {
                await _networkPeering.DisconnectPeerAsync(peerId).ConfigureAwait(false);
                return;
            }

            // check if we should request blocks
            var isPeerAhead = IsPeerAhead(peerPeeringStatus, beaconState);
            if (isPeerAhead)
            {
                // In theory, our chain since finalized checkpoint could be wrong
                // However it may be more efficient to check if our head is correct and sync from there,
                // or use the step option to sample blocks and find where we diverge.
                Slot finalizedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(beaconState.FinalizedCheckpoint.Epoch);

                if (_logger.IsInfo())
                    Log.RequestingBlocksFromAheadPeer(_logger, peerId, finalizedSlot, peerPeeringStatus.HeadRoot,
                        peerPeeringStatus.HeadSlot, null);

                // TODO: Need more sophistication, like Eth1; as peers are discovered, just put into a pool,
                // then, when need for sync determined, select the best peer(s) to use.

                await _networkPeering.RequestBlocksAsync(peerId, peerPeeringStatus.HeadRoot, finalizedSlot,
                    peerPeeringStatus.HeadSlot);
            }
            else
            {
                if (_logger.IsDebug())
                    LogDebug.PeerBehind(_logger, peerId, peerPeeringStatus.FinalizedEpoch, peerPeeringStatus.HeadRoot,
                        peerPeeringStatus.HeadSlot, null);
            }
        }

        private static bool IsPeerAhead(PeeringStatus peerPeeringStatus, BeaconState beaconState)
        {
            var isPeerAhead = beaconState.FinalizedCheckpoint.Epoch < peerPeeringStatus.FinalizedEpoch
                              || (beaconState.FinalizedCheckpoint.Epoch == peerPeeringStatus.FinalizedEpoch &&
                                  beaconState.Slot < peerPeeringStatus.HeadSlot);
            return isPeerAhead;
        }

        private async Task<bool> IsValidPeerStatus(string peerId, PeeringStatus peerPeeringStatus, Root headRoot,
            BeaconState beaconState)
        {
            // Check head epoch expected fork version
            Epoch peerEpoch = _beaconChainUtility.ComputeEpochAtSlot(peerPeeringStatus.HeadSlot);
            ForkVersion expectedForkVersionAtPeerEpoch;
            if (peerEpoch < beaconState.Fork.Epoch)
            {
                expectedForkVersionAtPeerEpoch = beaconState.Fork.PreviousVersion;
            }
            else
            {
                expectedForkVersionAtPeerEpoch = beaconState.Fork.CurrentVersion;
            }

            if (!peerPeeringStatus.HeadForkVersion.Equals(expectedForkVersionAtPeerEpoch))
            {
                if (_logger.IsWarn())
                    Log.PeerStatusInvalidForkVersion(_logger, peerId, peerPeeringStatus.HeadForkVersion,
                        peerPeeringStatus.HeadSlot,
                        peerEpoch, expectedForkVersionAtPeerEpoch, null);
                return false;
            }

            // Check finalized checkpoint in chain at expected epoch; only if finalized checkpoint is shared (i.e. also finalized for us) 
            if (peerPeeringStatus.FinalizedEpoch <= beaconState.FinalizedCheckpoint.Epoch)
            {
                // If the (finalized_root, finalized_epoch) shared by the peer is not in the client's chain at the expected epoch.
                // For example, if Peer 1 sends (root, epoch) of (A, 5) and Peer 2 sends (B, 3) but Peer 1 has root C at epoch 3,
                // then Peer 1 would disconnect because it knows that their chains are irreparably disjoint.

                Root expectedFinalizedRoot;
                if (peerPeeringStatus.FinalizedEpoch == Epoch.Zero)
                {
                    // The genesis checkpoint (epoch 0) has root zero.
                    expectedFinalizedRoot = Root.Zero;
                }
                else
                {
                    // Otherwise get the ancestor at the start slot of the peer's finalized epoch
                    Slot peerFinalizedSlot =
                        _beaconChainUtility.ComputeStartSlotOfEpoch(peerPeeringStatus.FinalizedEpoch);
                    expectedFinalizedRoot = await _forkChoice
                        .GetAncestorAsync(_store, headRoot, peerFinalizedSlot)
                        .ConfigureAwait(false);
                }

                if (!peerPeeringStatus.FinalizedRoot.Equals(expectedFinalizedRoot))
                {
                    if (_logger.IsWarn())
                        Log.PeerStatusInvalidFinalizedCheckpoint(_logger, peerId, peerPeeringStatus.FinalizedRoot,
                            peerPeeringStatus.FinalizedEpoch, expectedFinalizedRoot, null);
                    return false;
                }
            }

            return true;
        }
    }
}