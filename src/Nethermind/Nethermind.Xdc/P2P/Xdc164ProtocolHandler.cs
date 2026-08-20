// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// XDC's port of <c>eth/64</c> (EIP-2364): the fork ID handshake plus the XDC-only messages.
/// </summary>
internal class Xdc164ProtocolHandler(
    XdcConsensusMessageHandler.Factory consensusMessages,
    ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager nodeStatsManager,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxGossipPolicy? transactionsGossipPolicy = null) : Eth64ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy), IStaticProtocolInfo, IXdcConsensusPeer
{
    private readonly XdcConsensusMessageHandler _consensusMessages = consensusMessages.ForSession(session);

    public override string Name => "xdc164";

    public static byte Version => XdcProtocolVersions.Xdc164;
    public override byte ProtocolVersion => Version;

    public override int MessageIdSpaceSize => XdcProtocolVersions.LegacyMessageIdSpaceSize;

    protected override bool HandleMessageCore(ZeroPacket message) =>
        _consensusMessages.TryHandle(message, this) || base.HandleMessageCore(message);

    XdcConsensusMessageHandler IXdcConsensusPeer.ConsensusMessages => _consensusMessages;

    void IXdcConsensusPeer.Dispatch<T>(T message) => Send(message);

    T IXdcMessageContext.Decode<T>(IByteBuffer buffer) => Deserialize<T>(buffer);

    void IXdcMessageContext.Report(MessageBase message, int size) => ReportIn(message, size);

    void IXdcMessageContext.Report(string messageInfo, int size) => ReportIn(messageInfo, size);
}
