//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public class StatsParameters
    {
        private StatsParameters()
        {
            FailedConnectionDelays = new[] {100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 60000 * 5};
            DisconnectDelays = new[] {100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 60000 * 5};
        }

        public static StatsParameters Instance { get; } = new StatsParameters();

        public HashSet<DisconnectReason> PenalizedReputationLocalDisconnectReasons { get; set; } =
            new HashSet<DisconnectReason>
            {
                DisconnectReason.UnexpectedIdentity,
                DisconnectReason.IncompatibleP2PVersion,
                DisconnectReason.UselessPeer,
                DisconnectReason.BreachOfProtocol
            };

        public HashSet<DisconnectReason> PenalizedReputationRemoteDisconnectReasons { get; set; } =
            new HashSet<DisconnectReason>
            {
                DisconnectReason.UnexpectedIdentity,
                DisconnectReason.IncompatibleP2PVersion,
                DisconnectReason.UselessPeer,
                DisconnectReason.BreachOfProtocol,
                DisconnectReason.TooManyPeers,
                DisconnectReason.AlreadyConnected
            };

        public long PenalizedReputationTooManyPeersTimeout { get; } = 10 * 1000;

        public int[] FailedConnectionDelays { get; }

        public int[] DisconnectDelays { get; }
    }
}
