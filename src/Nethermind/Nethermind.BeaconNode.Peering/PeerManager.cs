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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public class PeerManager
    {
        private readonly ConcurrentQueue<string> _expectedPeers = new ConcurrentQueue<string>();
        private Slot _highestPeerSlot;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, PeerInfo> _peers =
            new ConcurrentDictionary<string, PeerInfo>();

        private readonly ConcurrentDictionary<string, ConcurrentBag<Session>> _sessions =
            new ConcurrentDictionary<string, ConcurrentBag<Session>>();

        public PeerManager(ILogger<PeerManager> logger)
        {
            _logger = logger;
        }

        public Slot HighestPeerSlot
        {
            get => _highestPeerSlot;
        }

        public IReadOnlyDictionary<string, PeerInfo> Peers => _peers;

        public IReadOnlyDictionary<string, ConcurrentBag<Session>> Sessions => _sessions;

        public Slot SyncStartingSlot { get; private set; }

        public void AddExpectedPeer(string enr)
        {
            if (_logger.IsDebug()) LogDebug.AddingExpectedPeer(_logger, enr, null);
            _expectedPeers.Enqueue(enr);
        }

        public Session AddPeerSession(string peerId)
        {
            PeerInfo peerInfo = _peers.GetOrAdd(peerId, key => new PeerInfo(key));

            Session session;
            if (_expectedPeers.TryDequeue(out string? enr))
            {
                // Mothra doesn't tell us if peer connected was outgoing or incoming
                // Also, the bootnode ENR is currently opaque and not parsed, so we can't match
                // Just assume if we have N bootnodes, then the first N connections are us dialing them.
                // We are the dialing client, so we should send Status
                session = new Session(ConnectionDirection.Out, peerInfo);
            }
            else
            {
                // They connected to us; wait for them to send Status
                session = new Session(ConnectionDirection.In, peerInfo);
            }

            ConcurrentBag<Session> peerSessionCollection =
                _sessions.GetOrAdd(peerId, key => new ConcurrentBag<Session>());
            peerSessionCollection.Add(session);

            if (_logger.IsDebug())
                LogDebug.CreatedPeerSession(_logger, peerId, session.Id, session.Direction, null);

            return session;
        }

        public void DisconnectSession(string peerId)
        {
            if (_sessions.TryGetValue(peerId, out ConcurrentBag<Session>? peerSessionCollection))
            {
                Session? session = peerSessionCollection!.FirstOrDefault();
                if (session != null)
                {
                    if (_logger.IsDebug())
                        LogDebug.DisconnectingPeerSession(_logger, peerId, session.Id, session.Direction, null);

                    session.Disconnect();
                }
            }
        }

        public Session OpenSession(PeerInfo peerInfo)
        {
            // Peer should have been created, with an incoming session, when connected.
            // If not, i.e. first knowledge is the status message, then create an incoming session
            ConcurrentBag<Session> peerSessionCollection = _sessions.GetOrAdd(peerInfo.Id,
                key => new ConcurrentBag<Session>(new[] {new Session(ConnectionDirection.In, peerInfo)}));

            // Not sure if Bag is correct here (insert/take); maybe some kind of queue or stack?
            Session session = peerSessionCollection.First();
            session.Open();

            if (_logger.IsDebug())
                LogDebug.OpenedPeerSession(_logger, peerInfo.Id, session.Id, session.Direction, null);

            return session;
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

        public PeerInfo UpdatePeerStatus(string peerId, PeeringStatus status)
        {
            var peerDetails = _peers.GetOrAdd(peerId, key => new PeerInfo(key));
            peerDetails.SetStatus(status);
            UpdateMostRecentSlot(status.HeadSlot);
            return peerDetails;
        }
    }
}