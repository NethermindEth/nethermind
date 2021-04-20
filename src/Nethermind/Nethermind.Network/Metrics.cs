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

using System.ComponentModel;

namespace Nethermind.Network
{
    public static class Metrics
    {
        [Description("Number of incoming connection.")]
        public static long IncomingConnections { get; set; }
        
        [Description("Number of outgoing connection.")]
        public static long OutgoingConnections { get; set; }
        
        [Description("Number of devp2p handshakes")]
        public static long Handshakes { get; set; }
        
        [Description("Number of devp2p handshke timeouts")]
        public static long HandshakeTimeouts { get; set; }
        
        [Description("Number of devp2p hello messages received")]
        public static long HellosReceived { get; set; }
        
        [Description("Number of devp2p hello messages sent")]
        public static long HellosSent { get; set; }
        
        [Description("Number of eth status messages received")]
        public static long StatusesReceived { get; set; }
        
        [Description("Number of eth status messages sent")]
        public static long StatusesSent { get; set; }
        // todo
        [Description("Number of les status messages received")]
        public static long LesStatusesReceived { get; set; }
        // todo
        [Description("Number of les status messages sent")]
        public static long LesStatusesSent { get; set; }

        [Description("Number of received disconnects due to breach of protocol")]
        public static long BreachOfProtocolDisconnects { get; set; }
        
        [Description("Number of received disconnects due to useless peer")]
        public static long UselessPeerDisconnects { get; set; }
        
        [Description("Number of received disconnects due to too many peers")]
        public static long TooManyPeersDisconnects { get; set; }
        
        [Description("Number of received disconnects due to already connected")]
        public static long AlreadyConnectedDisconnects { get; set; }
        
        [Description("Number of received disconnects due to incompatible devp2p version")]
        public static long IncompatibleP2PDisconnects { get; set; }
        
        [Description("Number of received disconnects due to request timeouts")]
        public static long ReceiveMessageTimeoutDisconnects { get; set; }
        
        [Description("Number of received disconnects due to peer identity information mismatch")]
        public static long UnexpectedIdentityDisconnects { get; set; }
        
        [Description("Number of received disconnects due to missing peer identity")]
        public static long NullNodeIdentityDisconnects { get; set; }
        
        [Description("Number of received disconnects due to client quitting")]
        public static long ClientQuittingDisconnects { get; set; }
        
        [Description("Number of received disconnects due to other reasons")]
        public static long OtherDisconnects { get; set; }
        
        [Description("Number of received disconnects due to disconnect requested")]
        public static long DisconnectRequestedDisconnects { get; set; }
        
        [Description("Number of received disconnects due to connecting to self")]
        public static long SameAsSelfDisconnects { get; set; }
        
        [Description("Number of disconnects due to TCP error")]
        public static long TcpSubsystemErrorDisconnects { get; set; }
        
        [Description("Number of sent disconnects due to breach of protocol")]
        public static long LocalBreachOfProtocolDisconnects { get; set; }
        
        [Description("Number of sent disconnects due to useless peer")]
        public static long LocalUselessPeerDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to breach of protocol")]
        public static long LocalTooManyPeersDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to already connected")]
        public static long LocalAlreadyConnectedDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to incompatible devp2p")]
        public static long LocalIncompatibleP2PDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to request timeout")]
        public static long LocalReceiveMessageTimeoutDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to node identity info mismatch")]
        public static long LocalUnexpectedIdentityDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to missing node identity")]
        public static long LocalNullNodeIdentityDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to client quitting")]
        public static long LocalClientQuittingDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to other reason")]
        public static long LocalOtherDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to disconnect requested")]
        public static long LocalDisconnectRequestedDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to connection to self")]
        public static long LocalSameAsSelfDisconnects { get; set; }
        
        [Description("Number of initiated disconnects due to TCP error")]
        public static long LocalTcpSubsystemErrorDisconnects { get; set; }
        
        [Description("Number of eth.62 NewBlockHashes messages received")]
        public static long Eth62NewBlockHashesReceived { get; set; }
        
        [Description("Number of eth.62 Transactions messages received")]
        public static long Eth62TransactionsReceived { get; set; }
        
        [Description("Number of eth.62 GetBlockHeaders messages received")]
        public static long Eth62GetBlockHeadersReceived { get; set; }
        
        [Description("Number of eth.62 BlockHeaders messages received")]
        public static long Eth62BlockHeadersReceived { get; set; }
        
        [Description("Number of eth.62 GetBlockBodies messages received")]
        public static long Eth62GetBlockBodiesReceived { get; set; }
        
        [Description("Number of eth.62 BlockBodies messages received")]
        public static long Eth62BlockBodiesReceived { get; set; }
        
        [Description("Number of eth.62 NewBlock messages received")]
        public static long Eth62NewBlockReceived { get; set; }
        
        [Description("Number of eth.63 GetReceipts messages received")]
        public static long Eth63GetReceiptsReceived { get; set; }
        
        [Description("Number of eth.63 Receipts messages received")]
        public static long Eth63ReceiptsReceived { get; set; }
        
        [Description("Number of eth.63 GetNodeData messages received")]
        public static long Eth63GetNodeDataReceived { get; set; }
        
        [Description("Number of eth.63 NodeData messages received")]
        public static long Eth63NodeDataReceived { get; set; }
        
        [Description("Number of eth.65 NewPooledTransactionHashes messages received")]
        public static long Eth65NewPooledTransactionHashesReceived { get; set; }

        [Description("Number of eth.65 GetPooledTransactions messages received")]
        public static long Eth65GetPooledTransactionsReceived { get; set; }
        
        [Description("Number of eth.65 GetPooledTransactions messages sent")]
        public static long Eth65GetPooledTransactionsRequested { get; set; }
        
        [Description("Number of eth.65 PooledTransactions messages received")]
        public static long Eth65PooledTransactionsReceived { get; set; }

        [Description("Number of bytes sent through P2P (TCP).")]
        public static long P2PBytesSent;

        [Description("Number of bytes received through P2P (TCP).")]
        public static long P2PBytesReceived;
        
        [Description("Number of bytes sent through Discovery (UDP).")]
        public static long DiscoveryBytesSent;

        [Description("Number of bytes received through Discovery (UDP).")]
        public static long DiscoveryBytesReceived;
    }
}
