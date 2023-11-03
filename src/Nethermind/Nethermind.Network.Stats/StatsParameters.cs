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

        public Dictionary<DisconnectReason, (TimeSpan ReconnectDelay, double ReputationMultiplier)> LocalDisconnectParams { get; } = new()
        {
            // Its actually protocol init timeout, when status message is not received in time.
            { DisconnectReason.ReceiveMessageTimeout, (TimeSpan.FromMinutes(5), 1.0)},

            // Failed could be just timeout, or not synced.
            { DisconnectReason.PeerRefreshFailed, (TimeSpan.FromMinutes(5), 0.5)},

            { DisconnectReason.Other, (TimeSpan.Zero, 0.8)},

            // These are like, very bad
            { DisconnectReason.UnexpectedIdentity, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.IncompatibleP2PVersion, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.UselessPeer, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.BreachOfProtocol, (TimeSpan.FromMinutes(15), 0.01) }
        };

        public Dictionary<DisconnectReason, (TimeSpan ReconnectDelay, double ReputationMultiplier)> RemoteDisconnectParams { get; } = new()
        {
            { DisconnectReason.ClientQuitting, (TimeSpan.FromMinutes(5), 0.1) },
            { DisconnectReason.TooManyPeers, (TimeSpan.FromMinutes(1), 0.3) },

            { DisconnectReason.Other, (TimeSpan.Zero, 0.8)},

            // These are like, very bad
            { DisconnectReason.UnexpectedIdentity, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.IncompatibleP2PVersion, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.UselessPeer, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.BreachOfProtocol, (TimeSpan.FromMinutes(15), 0.01) },
            { DisconnectReason.AlreadyConnected, (TimeSpan.Zero, 0.01) },
        };

        public Dictionary<NodeStatsEventType, (TimeSpan ReconnectDelay, double ReputationMultiplier)> EventParams { get; } = new()
        {
            // Geth have 30 second reconnect delay. So its useless to try again before that.
            { NodeStatsEventType.Connecting, (TimeSpan.FromSeconds(30), 1.0) },

            { NodeStatsEventType.ConnectionFailedTargetUnreachable, (TimeSpan.FromMinutes(15), 1.0) },
            { NodeStatsEventType.ConnectionFailed, (TimeSpan.FromMinutes(5), 0.2) },
            { NodeStatsEventType.SyncInitFailed, (TimeSpan.Zero, 0.3) },
            { NodeStatsEventType.SyncFailed, (TimeSpan.Zero, 0.4) },
        };
    }
}
