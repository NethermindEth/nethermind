// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    //TODO: consult on use of metric disabling flags!
    public static class Metrics
    {
        [KeyIsLabel("reason")]
        [Description("Number of local disconnects")]
        public static ConcurrentDictionary<DisconnectReason, long> LocalDisconnectsTotal { get; } = new();

        [KeyIsLabel("reason")]
        [Description("Number of remote disconnects")]
        public static ConcurrentDictionary<DisconnectReason, long> RemoteDisconnectsTotal { get; } = new();

        [CounterMetric]
        [Description("Number of incoming connection.")]
        public static long IncomingConnections { get; set; }

        [CounterMetric]
        [Description("Number of outgoing connection.")]
        public static long OutgoingConnections { get; set; }

        [CounterMetric]
        [Description("Number of devp2p handshakes")]
        public static long Handshakes { get; set; }

        [CounterMetric]
        [Description("Number of devp2p handshake timeouts")]
        public static long HandshakeTimeouts { get; set; }

        [CounterMetric]
        [Description("_Deprecated._ Number of bytes sent through P2P (TCP).")]
        public static long P2PBytesSent;

        [CounterMetric]
        [Description("_Deprecated._ Number of bytes received through P2P (TCP).")]
        public static long P2PBytesReceived;

        [CounterMetric]
        [Description("Number of bytes sent through Discovery (UDP).")]
        public static long DiscoveryBytesSent;

        [CounterMetric]
        [Description("Number of bytes received through Discovery (UDP).")]
        public static long DiscoveryBytesReceived;

        [CounterMetric]
        [Description("Number of sent discovery message")]
        [DetailedMetric]
        [KeyIsLabel("message_type")]
        public static NonBlocking.ConcurrentDictionary<MsgType, long> DiscoveryMessagesSent { get; } = new();

        [CounterMetric]
        [Description("Number of sent discovery message")]
        [DetailedMetric]
        [KeyIsLabel("message_type")]
        public static NonBlocking.ConcurrentDictionary<MsgType, long> DiscoveryMessagesReceived { get; } = new();

        [GaugeMetric]
        //EIP-2159: Common Prometheus Metrics Names for Clients
        [Description("The current number of peers connected.")]
        [DataMember(Name = "ethereum_peer_count")]
        //The current number of peers connected changed by threadsafe atomic increment/decrement
        public static long PeerCount;

        [GaugeMetric]
        //EIP-2159: Common Prometheus Metrics Names for Clients
        [Description("The maximum number of peers this node allows to connect.")]
        [DataMember(Name = "ethereum_peer_limit")]
        public static long PeerLimit { get; set; }

        [CounterMetric]
        [DataMember(Name = "nethermind_outgoing_p2p_messages")]
        [Description("Number of outgoing p2p packets.")]
        [KeyIsLabel("protocol", "message")]
        public static NonBlocking.ConcurrentDictionary<P2PMessageKey, long> OutgoingP2PMessages { get; } = new();

        [CounterMetric]
        [DataMember(Name = "nethermind_outgoing_p2p_message_bytes")]
        [Description("Bytes of outgoing p2p packets.")]
        [KeyIsLabel("protocol", "message")]
        public static NonBlocking.ConcurrentDictionary<P2PMessageKey, long> OutgoingP2PMessageBytes { get; } = new();

        [CounterMetric]
        [DataMember(Name = "nethermind_incoming_p2p_messages")]
        [Description("Number of incoming p2p packets.")]
        [KeyIsLabel("protocol", "message")]
        public static NonBlocking.ConcurrentDictionary<P2PMessageKey, long> IncomingP2PMessages { get; } = new();

        [CounterMetric]
        [DataMember(Name = "nethermind_incoming_p2p_message_bytes")]
        [Description("Bytes of incoming p2p packets.")]
        [KeyIsLabel("protocol", "message")]
        public static NonBlocking.ConcurrentDictionary<P2PMessageKey, long> IncomingP2PMessageBytes { get; } = new();

        [CounterMetric]
        [Description("Number of candidate peers in peer manager")]
        [DetailedMetric]
        public static int PeerCandidateCount { get; set; }

        [CounterMetric]
        [Description("Number of filter reason per peer candidate")]
        [DetailedMetric]
        [KeyIsLabel("filter")]
        public static NonBlocking.ConcurrentDictionary<string, long> PeerCandidateFilter { get; } = new();
    }
}
