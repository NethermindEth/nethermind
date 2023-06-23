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

        public HashSet<EthDisconnectReason> PenalizedReputationLocalDisconnectReasons { get; set; } =
            new HashSet<EthDisconnectReason>
            {
                EthDisconnectReason.UnexpectedIdentity,
                EthDisconnectReason.IncompatibleP2PVersion,
                EthDisconnectReason.UselessPeer,
                EthDisconnectReason.BreachOfProtocol
            };

        public HashSet<EthDisconnectReason> PenalizedReputationRemoteDisconnectReasons { get; set; } =
            new HashSet<EthDisconnectReason>
            {
                EthDisconnectReason.UnexpectedIdentity,
                EthDisconnectReason.IncompatibleP2PVersion,
                EthDisconnectReason.UselessPeer,
                EthDisconnectReason.BreachOfProtocol,
                EthDisconnectReason.TooManyPeers,
                EthDisconnectReason.AlreadyConnected
            };

        public long PenalizedReputationTooManyPeersTimeout { get; } = 10 * 1000;

        public int[] FailedConnectionDelays { get; }

        public int[] DisconnectDelays { get; set; }

        public Dictionary<EthDisconnectReason, TimeSpan> DelayDueToLocalDisconnect { get; } = new()
        {
            { EthDisconnectReason.UselessPeer, TimeSpan.FromMinutes(15) },

            // Its actually protocol init timeout, when status message is not received in time.
            { EthDisconnectReason.ReceiveMessageTimeout, TimeSpan.FromMinutes(5) },
        };

        public Dictionary<EthDisconnectReason, TimeSpan> DelayDueToRemoteDisconnect { get; } = new()
        {
            { EthDisconnectReason.ClientQuitting, TimeSpan.FromMinutes(1) },
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
