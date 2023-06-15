// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;

namespace Nethermind.Network
{
    //TODO: consult on use of metric disabeling flags!
    public static class Metrics
    {
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
        [Description("Number of devp2p handshke timeouts")]
        public static long HandshakeTimeouts { get; set; }

        [CounterMetric]
        [Description("Number of devp2p hello messages received")]
        public static long HellosReceived { get; set; }

        [CounterMetric]
        [Description("Number of devp2p hello messages sent")]
        public static long HellosSent { get; set; }

        [CounterMetric]
        [Description("Number of eth status messages received")]
        public static long StatusesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth status messages sent")]
        public static long StatusesSent { get; set; }

        [CounterMetric]
        [Description("Number of les status messages received")]
        public static long LesStatusesReceived { get; set; }

        [CounterMetric]
        [Description("Number of les status messages sent")]
        public static long LesStatusesSent { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to breach of protocol")]
        public static long BreachOfProtocolDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to useless peer")]
        public static long UselessPeerDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to too many peers")]
        public static long TooManyPeersDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to already connected")]
        public static long AlreadyConnectedDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to incompatible devp2p version")]
        public static long IncompatibleP2PDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to request timeouts")]
        public static long ReceiveMessageTimeoutDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to peer identity information mismatch")]
        public static long UnexpectedIdentityDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to missing peer identity")]
        public static long NullNodeIdentityDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to client quitting")]
        public static long ClientQuittingDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to other reasons")]
        public static long OtherDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to disconnect requested")]
        public static long DisconnectRequestedDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of received disconnects due to connecting to self")]
        public static long SameAsSelfDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of disconnects due to TCP error")]
        public static long TcpSubsystemErrorDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of sent disconnects due to breach of protocol")]
        public static long LocalBreachOfProtocolDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of sent disconnects due to useless peer")]
        public static long LocalUselessPeerDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to breach of protocol")]
        public static long LocalTooManyPeersDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to already connected")]
        public static long LocalAlreadyConnectedDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to incompatible devp2p")]
        public static long LocalIncompatibleP2PDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to request timeout")]
        public static long LocalReceiveMessageTimeoutDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to node identity info mismatch")]
        public static long LocalUnexpectedIdentityDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to missing node identity")]
        public static long LocalNullNodeIdentityDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to client quitting")]
        public static long LocalClientQuittingDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to other reason")]
        public static long LocalOtherDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to disconnect requested")]
        public static long LocalDisconnectRequestedDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to connection to self")]
        public static long LocalSameAsSelfDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of initiated disconnects due to TCP error")]
        public static long LocalTcpSubsystemErrorDisconnects { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 NewBlockHashes messages received")]
        public static long Eth62NewBlockHashesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 Transactions messages received")]
        public static long Eth62TransactionsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 GetBlockHeaders messages received")]
        public static long Eth62GetBlockHeadersReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 BlockHeaders messages received")]
        public static long Eth62BlockHeadersReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 GetBlockBodies messages received")]
        public static long Eth62GetBlockBodiesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 BlockBodies messages received")]
        public static long Eth62BlockBodiesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.62 NewBlock messages received")]
        public static long Eth62NewBlockReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.63 GetReceipts messages received")]
        public static long Eth63GetReceiptsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.63 Receipts messages received")]
        public static long Eth63ReceiptsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.63 GetNodeData messages received")]
        public static long Eth63GetNodeDataReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.63 NodeData messages received")]
        public static long Eth63NodeDataReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.65 NewPooledTransactionHashes messages received")]
        public static long Eth65NewPooledTransactionHashesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.65 NewPooledTransactionHashes messages sent")]
        public static long Eth65NewPooledTransactionHashesSent { get; set; }

        [CounterMetric]
        [Description("Number of eth.68 NewPooledTransactionHashes messages received")]
        public static long Eth68NewPooledTransactionHashesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.68 NewPooledTransactionHashes messages sent")]
        public static long Eth68NewPooledTransactionHashesSent { get; set; }

        [CounterMetric]
        [Description("Number of eth.65 GetPooledTransactions messages received")]
        public static long Eth65GetPooledTransactionsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.65 GetPooledTransactions messages sent")]
        public static long Eth65GetPooledTransactionsRequested { get; set; }

        [CounterMetric]
        [Description("Number of eth.65 PooledTransactions messages received")]
        public static long Eth65PooledTransactionsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 GetBlockHeaders messages received")]
        public static long Eth66GetBlockHeadersReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 BlockHeaders messages received")]
        public static long Eth66BlockHeadersReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 GetBlockBodies messages received")]
        public static long Eth66GetBlockBodiesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 BlockBodies messages received")]
        public static long Eth66BlockBodiesReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 GetNodeData messages received")]
        public static long Eth66GetNodeDataReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 NodeData messages received")]
        public static long Eth66NodeDataReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 GetReceipts messages received")]
        public static long Eth66GetReceiptsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 Receipts messages received")]
        public static long Eth66ReceiptsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 GetPooledTransactions messages received")]
        public static long Eth66GetPooledTransactionsReceived { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 GetPooledTransactions messages sent")]
        public static long Eth66GetPooledTransactionsRequested { get; set; }

        [CounterMetric]
        [Description("Number of eth.66 PooledTransactions messages received")]
        public static long Eth66PooledTransactionsReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetAccountRange messages received")]
        public static long SnapGetAccountRangeReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetAccountRange messages sent")]
        public static long SnapGetAccountRangeSent { get; set; }

        [CounterMetric]
        [Description("Number of SNAP AccountRange messages received")]
        public static long SnapAccountRangeReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetStorageRanges messages received")]
        public static long SnapGetStorageRangesReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetStorageRanges messages sent")]
        public static long SnapGetStorageRangesSent { get; set; }

        [CounterMetric]
        [Description("Number of SNAP StorageRanges messages received")]
        public static long SnapStorageRangesReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetByteCodes messages received")]
        public static long SnapGetByteCodesReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetByteCodes messages sent")]
        public static long SnapGetByteCodesSent { get; set; }

        [CounterMetric]
        [Description("Number of SNAP ByteCodes messages received")]
        public static long SnapByteCodesReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetTrieNodes messages received")]
        public static long SnapGetTrieNodesReceived { get; set; }

        [CounterMetric]
        [Description("Number of SNAP GetTrieNodes messages sent")]
        public static long SnapGetTrieNodesSent { get; set; }

        [CounterMetric]
        [Description("Number of SNAP TrieNodes messages received")]
        public static long SnapTrieNodesReceived { get; set; }

        [CounterMetric]
        [Description("Number of GetNodeData messages received via NodeData protocol")]
        public static long GetNodeDataReceived { get; set; }

        [CounterMetric]
        [Description("Number of NodeData messages received via NodeData protocol")]
        public static long NodeDataReceived { get; set; }

        [CounterMetric]
        [Description("Number of bytes sent through P2P (TCP).")]
        public static long P2PBytesSent;

        [CounterMetric]
        [Description("Number of bytes received through P2P (TCP).")]
        public static long P2PBytesReceived;

        [CounterMetric]
        [Description("Number of bytes sent through Discovery (UDP).")]
        public static long DiscoveryBytesSent;

        [CounterMetric]
        [Description("Number of bytes received through Discovery (UDP).")]
        public static long DiscoveryBytesReceived;

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
    }
}
