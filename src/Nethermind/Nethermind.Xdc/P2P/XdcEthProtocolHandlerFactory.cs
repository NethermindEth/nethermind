// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// Factory for creating XDC-specific ETH protocol handlers (versions 62-65 and 100).
/// Registered with AddFirst to intercept ETH protocol creation before the default factory.
/// </summary>
public class XdcEthProtocolHandlerFactory(
    ITimeoutCertificateManager timeoutCertificateManager,
    IVotesManager votesManager,
    ISyncInfoManager syncInfoManager,
    IMessageSerializationService serializer,
    INodeStatsManager stats,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxGossipPolicy txGossipPolicy) : IProtocolHandlerFactory
{
    public string ProtocolCode => Protocol.Eth;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        handler = version switch
        {
            62 => new Eth62ProtocolHandler(session, serializer, stats, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, logManager, txGossipPolicy),
            63 => new Eth63ProtocolHandler(session, serializer, stats, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, logManager, txGossipPolicy),
            64 => new Eth64ProtocolHandler(session, serializer, stats, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txGossipPolicy),
            65 => new Eth65ProtocolHandler(session, serializer, stats, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txGossipPolicy),
            100 => new XdcProtocolHandler(timeoutCertificateManager, votesManager, syncInfoManager, session, serializer, stats, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, txGossipPolicy),
            _ => null
        };

        return handler is not null;
    }
}
