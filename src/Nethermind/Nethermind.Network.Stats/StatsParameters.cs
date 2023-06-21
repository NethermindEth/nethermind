// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public class StatsParameters
    {
        private StatsParameters()
        {
            FailedConnectionDelays = new[] { 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 60000 * 5 };
            DisconnectDelays = new[] { 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 60000 * 5 };
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

        public int[] DisconnectDelays { get; set;  }

        public Dictionary<DisconnectReason, TimeSpan> DelayDueToLocalDisconnect { get; } = new()
        {
            { DisconnectReason.UselessPeer, TimeSpan.FromMinutes(15) },

            // Its actually protocol init timeout, when status message is not received in time.
            { DisconnectReason.ReceiveMessageTimeout, TimeSpan.FromMinutes(5) },
        };

        public Dictionary<DisconnectReason, TimeSpan> DelayDueToRemoteDisconnect { get; } = new()
        {
            // Actual explicit ClientQuitting is very rare, but internally we also use this status for connection
            // closed, which can happen if remote client close connection without giving any reason.
            // It is unclear why we have such large number of these, but it seems that it is usually transient.
            { DisconnectReason.ClientQuitting, TimeSpan.FromMinutes(1) },

            // This is pretty much 80% of the disconnect reason. We don't wanna delay this though... it could be
            // that other peer disconnect from the peer.
            { DisconnectReason.TooManyPeers, TimeSpan.FromMinutes(1) },
        };

        public Dictionary<NodeStatsEventType, TimeSpan> DelayDueToEvent { get; } = new()
        {
            // Geth have 30 second reconnect delay. So its useless to try again before that.
            { NodeStatsEventType.Connecting, TimeSpan.FromSeconds(30) },

            { NodeStatsEventType.ConnectionFailedTargetUnreachable, TimeSpan.FromMinutes(15) },
            { NodeStatsEventType.ConnectionFailed, TimeSpan.FromMinutes(5) },
        };
    }
}
