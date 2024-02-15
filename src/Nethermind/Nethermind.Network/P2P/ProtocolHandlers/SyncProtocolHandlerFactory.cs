// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public abstract class SyncProtocolHandlerFactory: IProtocolHandlerFactory
{
    private readonly IProtocolValidator _protocolValidator;
    private readonly INetworkStorage _peerStorage;
    private readonly ISyncPeerPool _syncPool;
    protected readonly ITxPool _txPool;
    protected readonly INodeStatsManager _stats;
    private readonly ILogger _logger;

    protected SyncProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        INetworkStorage peerStorage,
        ISyncPeerPool syncPool,
        ITxPool txPool,
        INodeStatsManager stats,
        ILogManager logManager)
    {
        _protocolValidator = protocolValidator;
        _peerStorage = peerStorage;
        _syncPool = syncPool;
        _txPool = txPool;
        _stats = stats;
        _logger = logManager.GetClassLogger<SyncProtocolHandlerFactory>();
    }

    public int ProtocolPriority => ProtocolPriorities.Sync;
    public abstract int MessageIdSpaceSize { get; }

    protected abstract SyncPeerProtocolHandlerBase CreateSyncProtocolHandler(ISession session);

    public IProtocolHandler Create(ISession session)
    {
        SyncPeerProtocolHandlerBase handler = CreateSyncProtocolHandler(session);
        InitSyncPeerProtocol(session, handler);
        return handler;
    }

    private void InitSyncPeerProtocol(ISession session, SyncPeerProtocolHandlerBase handler)
    {
        session.Node.EthDetails = handler.Name;
        handler.ProtocolInitialized += (sender, args) =>
        {
            if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
            SyncPeerProtocolInitializedEventArgs typedArgs = (SyncPeerProtocolInitializedEventArgs)args;
            // TODO: Does not need to be here
            _stats.ReportSyncPeerInitializeEvent(handler.ProtocolCode, session.Node, new SyncPeerNodeDetails
            {
                NetworkId = typedArgs.NetworkId,
                BestHash = typedArgs.BestHash,
                GenesisHash = typedArgs.GenesisHash,
                ProtocolVersion = typedArgs.ProtocolVersion,
                TotalDifficulty = (BigInteger)typedArgs.TotalDifficulty
            });
            bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
            if (isValid)
            {
                // TODO: Disconnect if pool already have peer
                // if (_logger.IsTrace) _logger.Trace($"Not able to add a sync peer on {session} for {session.Node:s}");
                // session.InitiateDisconnect(DisconnectReason.SessionIdAlreadyExists, "sync peer");
                _syncPool.AddPeer(handler);
                if (handler.IncludeInTxPool) _txPool.AddPeer(handler);
                if (_logger.IsDebug) _logger.Debug($"{handler.ClientId} sync peer {session} created.");

                session.Disconnected += (o, e) =>
                {
                    _syncPool.RemovePeer(handler);
                    _txPool.RemovePeer(session.Node.Id);
                    if (session.BestStateReached == SessionState.Initialized)
                    {
                        if (_logger.IsDebug)
                            _logger.Debug(
                                $"{session.Direction} {session.Node:s} disconnected {e.DisconnectType} {e.DisconnectReason} {e.Details}");
                    }
                };

                if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");

                //Add/Update peer to the storage and to sync manager
                _peerStorage.UpdateNode(new NetworkNode(session.Node.Id, session.Node.Host, session.Node.Port, _stats.GetOrAdd(session.Node).NewPersistedNodeReputation(DateTime.UtcNow)));
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
            }
        };
    }

    private bool RunBasicChecks(ISession session, string protocolCode, int protocolVersion)
    {
        if (session.IsClosing)
        {
            if (_logger.IsDebug) _logger.Debug($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
            return false;
        }

        if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
        return true;
    }
}
