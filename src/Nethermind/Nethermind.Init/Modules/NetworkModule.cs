// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Handshake = Nethermind.Network.Rlpx.Handshake;
using P2P = Nethermind.Network.P2P.Messages;
using V62 = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using V63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using V65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using V66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using V68 = Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using NodeData = Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using Snap = Nethermind.Network.P2P.Subprotocols.Snap.Messages;

namespace Nethermind.Init.Modules;

public class NetworkModule(IConfigProvider configProvider) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))

            // Rlpxhost
            .AddSingleton<IDisconnectsAnalyzer, MetricsDisconnectsAnalyzer>()
            .AddSingleton<ISessionMonitor, SessionMonitor>()
            .AddSingleton<IRlpxHost, RlpxHost>()
            .AddSingleton<Handshake.IHandshakeService, Handshake.HandshakeService>()

            .AddSingleton<INodeStatsManager>(ctx => new NodeStatsManager(
                ctx.Resolve<ITimerFactory>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<INetworkConfig>()
                    .MaxCandidatePeerCount)) // The INetworkConfig is not referable in NodeStatsManager.

            .AddSingleton<IMessageSerializationService, MessageSerializationService>()
            .AddSingleton<IMessagePad, Handshake.Eip8MessagePad>()

            // Handshake
            .AddMessageSerializer<Handshake.AuthEip8Message, Handshake.AuthEip8MessageSerializer>()
            .AddMessageSerializer<Handshake.AckEip8Message, Handshake.AckEip8MessageSerializer>()
            .AddMessageSerializer<Handshake.AckMessage, Handshake.AckMessageSerializer>()
            .AddMessageSerializer<Handshake.AuthMessage, Handshake.AuthMessageSerializer>()

            // P2P
            .AddMessageSerializer<P2P.AddCapabilityMessage, P2P.AddCapabilityMessageSerializer>()
            .AddMessageSerializer<P2P.DisconnectMessage, P2P.DisconnectMessageSerializer>()
            .AddMessageSerializer<P2P.HelloMessage, P2P.HelloMessageSerializer>()
            .AddMessageSerializer<P2P.PingMessage, P2P.PingMessageSerializer>()
            .AddMessageSerializer<P2P.PongMessage, P2P.PongMessageSerializer>()

            // NodeData
            .AddMessageSerializer<NodeData.GetNodeDataMessage, NodeData.GetNodeDataMessageSerializer>()
            .AddMessageSerializer<NodeData.NodeDataMessage, NodeData.NodeDataMessageSerializer>()

            // Snap
            .AddMessageSerializer<Snap.AccountRangeMessage, Snap.AccountRangeMessageSerializer>()
            .AddMessageSerializer<Snap.ByteCodesMessage, Snap.ByteCodesMessageSerializer>()
            .AddMessageSerializer<Snap.GetAccountRangeMessage, Snap.GetAccountRangeMessageSerializer>()
            .AddMessageSerializer<Snap.GetByteCodesMessage, Snap.GetByteCodesMessageSerializer>()
            .AddMessageSerializer<Snap.GetStorageRangeMessage, Snap.GetStorageRangesMessageSerializer>()
            .AddMessageSerializer<Snap.GetTrieNodesMessage, Snap.GetTrieNodesMessageSerializer>()
            .AddMessageSerializer<Snap.StorageRangeMessage, Snap.StorageRangesMessageSerializer>()
            .AddMessageSerializer<Snap.TrieNodesMessage, Snap.TrieNodesMessageSerializer>()

            // V62
            .AddMessageSerializer<V62.BlockBodiesMessage, V62.BlockBodiesMessageSerializer>()
            .AddMessageSerializer<V62.BlockHeadersMessage, V62.BlockHeadersMessageSerializer>()
            .AddMessageSerializer<V62.GetBlockBodiesMessage, V62.GetBlockBodiesMessageSerializer>()
            .AddMessageSerializer<V62.GetBlockHeadersMessage, V62.GetBlockHeadersMessageSerializer>()
            .AddMessageSerializer<V62.NewBlockHashesMessage, V62.NewBlockHashesMessageSerializer>()
            .AddMessageSerializer<V62.NewBlockMessage, V62.NewBlockMessageSerializer>()
            .AddMessageSerializer<V62.StatusMessage, V62.StatusMessageSerializer>()
            .AddMessageSerializer<V62.TransactionsMessage, V62.TransactionsMessageSerializer>()

            // V63
            .AddMessageSerializer<V63.GetNodeDataMessage, V63.GetNodeDataMessageSerializer>()
            .AddMessageSerializer<V63.GetReceiptsMessage, V63.GetReceiptsMessageSerializer>()
            .AddMessageSerializer<V63.NodeDataMessage, V63.NodeDataMessageSerializer>()
            .AddMessageSerializer<V63.ReceiptsMessage, V63.ReceiptsMessageSerializer>()
            .AddSingleton<IZeroInnerMessageSerializer<V63.ReceiptsMessage>, V63.ReceiptsMessageSerializer>() // For v66 receipt

            // V65
            .AddMessageSerializer<V65.GetPooledTransactionsMessage, V65.GetPooledTransactionsMessageSerializer>()
            .AddMessageSerializer<V65.NewPooledTransactionHashesMessage, V65.NewPooledTransactionHashesMessageSerializer>()
            .AddMessageSerializer<V65.PooledTransactionsMessage, V65.PooledTransactionsMessageSerializer>()

            // V66
            .AddMessageSerializer<V66.BlockBodiesMessage, V66.BlockBodiesMessageSerializer>()
            .AddMessageSerializer<V66.BlockHeadersMessage, V66.BlockHeadersMessageSerializer>()
            .AddMessageSerializer<V66.GetBlockBodiesMessage, V66.GetBlockBodiesMessageSerializer>()
            .AddMessageSerializer<V66.GetBlockHeadersMessage, V66.GetBlockHeadersMessageSerializer>()
            .AddMessageSerializer<V66.GetNodeDataMessage, V66.GetNodeDataMessageSerializer>()
            .AddMessageSerializer<V66.GetPooledTransactionsMessage, V66.GetPooledTransactionsMessageSerializer>()
            .AddMessageSerializer<V66.GetReceiptsMessage, V66.GetReceiptsMessageSerializer>()
            .AddMessageSerializer<V66.NodeDataMessage, V66.NodeDataMessageSerializer>()
            .AddMessageSerializer<V66.PooledTransactionsMessage, V66.PooledTransactionsMessageSerializer>()
            .AddMessageSerializer<V66.ReceiptsMessage, V66.ReceiptsMessageSerializer>()

            // V68
            .AddMessageSerializer<V68.NewPooledTransactionHashesMessage68, V68.NewPooledTransactionHashesMessageSerializer>()

            ;
    }
}
