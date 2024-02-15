// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public abstract class SatelliteProtocolHandlerFactory: IProtocolHandlerFactory
{
    private readonly ILogger _logger;
    private readonly IProtocolValidator _protocolValidator;
    private readonly ISyncPeerPool _syncPool;

    protected SatelliteProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        ISyncPeerPool syncPool,
        ILogManager logManager)
    {
        _protocolValidator = protocolValidator;
        _syncPool = syncPool;
        _logger = logManager.GetClassLogger<SyncProtocolHandlerFactory>();
    }


    public int ProtocolPriority => ProtocolPriorities.Satellite;
    public abstract int MessageIdSpaceSize { get; }
    public abstract ProtocolHandlerBase CreateSatelliteProtocol(ISession session);

    public IProtocolHandler Create(ISession session)
    {
        ProtocolHandlerBase satelliteProtocol = CreateSatelliteProtocol(session);
        InitSatelliteProtocol(session, satelliteProtocol);
        return satelliteProtocol;
    }


    private void InitSatelliteProtocol(ISession session, ProtocolHandlerBase handler)
    {
        session.Node.EthDetails = handler.Name;
        handler.ProtocolInitialized += (sender, args) =>
        {
            if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
            // SyncPeerProtocolInitializedEventArgs typedArgs = (SyncPeerProtocolInitializedEventArgs)args;
            // _stats.ReportSyncPeerInitializeEvent(handler.ProtocolCode, session.Node, new SyncPeerNodeDetails
            // {
            //     NetworkId = typedArgs.NetworkId,
            //     BestHash = typedArgs.BestHash,
            //     GenesisHash = typedArgs.GenesisHash,
            //     ProtocolVersion = typedArgs.ProtocolVersion,
            //     TotalDifficulty = (BigInteger)typedArgs.TotalDifficulty
            // });
            bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
            if (isValid)
            {
                var peer = _syncPool.GetPeer(session.Node)!;
                peer.SyncPeer.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
                if (handler.IsPriority) _syncPool.SetPeerPriority(session.Node.Id);
                if (_logger.IsDebug) _logger.Debug($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}.");

                if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");
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
