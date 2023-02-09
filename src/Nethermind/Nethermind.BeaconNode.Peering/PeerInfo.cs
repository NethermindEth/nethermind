// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core2.P2p;

namespace Nethermind.BeaconNode.Peering
{
    public class PeerInfo
    {
        public PeerInfo(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public PeeringStatus? Status { get; private set; }

        public void SetStatus(PeeringStatus peeringStatus)
        {
            Status = peeringStatus;
        }
    }
}
