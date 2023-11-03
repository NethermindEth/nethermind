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

        public int[] FailedConnectionDelays { get; }

        public int[] DisconnectDelays { get; set; }

        public Dictionary<DisconnectReason, (TimeSpan ReconnectDelay, long ReputationScore)> LocalDisconnectParams { get; } = new()
        {
            // Its actually protocol init timeout, when status message is not received in time.
            { DisconnectReason.ReceiveMessageTimeout, (TimeSpan.FromMinutes(5), 0)},

            // Failed could be just timeout, or not synced.
            { DisconnectReason.PeerRefreshFailed, (TimeSpan.FromMinutes(5), -500)},

            { DisconnectReason.Other, (TimeSpan.Zero, -200)},

            // These are like, very bad
            { DisconnectReason.UnexpectedIdentity, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.IncompatibleP2PVersion, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.UselessPeer, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.BreachOfProtocol, (TimeSpan.FromMinutes(15), -10000) }
        };

        public Dictionary<DisconnectReason, (TimeSpan ReconnectDelay, long ReputationScore)> RemoteDisconnectParams { get; } = new()
        {
            { DisconnectReason.ClientQuitting, (TimeSpan.FromMinutes(5), -1000) },
            { DisconnectReason.TooManyPeers, (TimeSpan.FromMinutes(1), -300) },

            { DisconnectReason.Other, (TimeSpan.Zero, -200)},

            // These are like, very bad
            { DisconnectReason.UnexpectedIdentity, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.IncompatibleP2PVersion, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.UselessPeer, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.BreachOfProtocol, (TimeSpan.FromMinutes(15), -10000) },
            { DisconnectReason.AlreadyConnected, (TimeSpan.Zero, -10000) },
        };

        public Dictionary<NodeStatsEventType, (TimeSpan ReconnectDelay, long ReputationScore)> EventParams { get; } = new()
        {
            // Geth have 30 second reconnect delay. So its useless to try again before that.
            { NodeStatsEventType.Connecting, (TimeSpan.FromSeconds(30), 0) },

            { NodeStatsEventType.ConnectionFailedTargetUnreachable, (TimeSpan.FromMinutes(15), 0) },
            { NodeStatsEventType.ConnectionFailed, (TimeSpan.FromMinutes(5), -1000) },
            { NodeStatsEventType.SyncInitFailed, (TimeSpan.Zero, -300) },
            { NodeStatsEventType.SyncFailed, (TimeSpan.Zero, -500) },

            // These are positive
            { NodeStatsEventType.HandshakeCompleted, (TimeSpan.Zero, 10) },
            { NodeStatsEventType.P2PInitialized, (TimeSpan.Zero, 10) },
            { NodeStatsEventType.Eth62Initialized, (TimeSpan.Zero, 20) },
            { NodeStatsEventType.SyncStarted, (TimeSpan.Zero, 1000) },
            { NodeStatsEventType.DiscoveryPingIn, (TimeSpan.Zero, 500) },
            { NodeStatsEventType.DiscoveryNeighboursIn, (TimeSpan.Zero, 500) },
        };
    }
}
