// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconNode.Peering
{
    public class Session
    {
        public Session(ConnectionDirection direction, PeerInfo peer)
        {
            Direction = direction;
            Peer = peer;
            State = SessionState.New;
            Id = Guid.NewGuid();
        }

        public ConnectionDirection Direction { get; }

        public Guid Id { get; }

        public PeerInfo Peer { get; }

        public SessionState State { get; private set; }

        public void Disconnect()
        {
            State = SessionState.Disconnecting;
        }

        public void Open()
        {
            State = SessionState.Open;
        }
    }
}
