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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Peering
{
    public class PeerManager
    {
        private readonly ConcurrentQueue<string> _expectedPeers = new ConcurrentQueue<string>();
        private Slot _highestPeerSlot;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, PeerDetails> _peers =
            new ConcurrentDictionary<string, PeerDetails>();

        private readonly Synchronization _synchronization;

        public PeerManager(ILogger<PeerManager> logger,
            Synchronization synchronization)
        {
            _logger = logger;
            _synchronization = synchronization;
        }

        public Slot HighestPeerSlot
        {
            get => _highestPeerSlot;
        }

        public Slot SyncStartingSlot { get; private set; }

        public void AddExpectedPeer(string enr)
        {
            _expectedPeers.Enqueue(enr);
        }

        public bool AddPeer(string peerId)
        {
            PeerDetails peerDetails = new PeerDetails(peerId);
            if (_peers.TryAdd(peerId, peerDetails))
            {
                if (_expectedPeers.TryDequeue(out string enr))
                {
                    // Mothra doesn't tell us if peer connected was outgoing or incoming
                    // Also, the bootnode ENR is currently opaque and not parsed, so we can't match
                    // Just assume if we have N bootnodes, then the first N connections are us dialing them.
                    // We are the dialing client, so we should send Status
                    peerDetails.SetDialDirection(DialDirection.DialOut);
                    return true;
                }
                else
                {
                    // They connected to us; wait for them to send Status
                    peerDetails.SetDialDirection(DialDirection.DialIn);
                }
            }
            else
            {
                // Peer already existed
            }

            return false;
        }

        public void StartSync(Slot slot)
        {
            SyncStartingSlot = slot;
        }

        public void UpdateMostRecentSlot(Slot slot)
        {
            Slot initialValue;
            do
            {
                initialValue = _highestPeerSlot;
                if (slot <= initialValue)
                {
                    break;
                }
            } while (initialValue != Slot.InterlockedCompareExchange(ref _highestPeerSlot, slot, initialValue));
        }

        public void UpdatePeerStatus(string peerId, PeeringStatus status)
        {
            PeerDetails emptyPeerDetails = new PeerDetails(peerId);
            var peerDetails = _peers.GetOrAdd(peerId, emptyPeerDetails);
            peerDetails.SetStatus(status);
            UpdateMostRecentSlot(status.HeadSlot);
        }
    }
}