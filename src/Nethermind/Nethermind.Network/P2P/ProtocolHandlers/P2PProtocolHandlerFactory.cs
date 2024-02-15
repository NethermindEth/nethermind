// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class P2PProtocolHandlerFactory: IProtocolHandlerFactory
{
    private readonly IRlpxHost _rlpxHost;
    private readonly IMessageSerializationService _serializer;
    private readonly Regex? _clientIdPattern;
    private readonly IDiscoveryApp _discoveryApp;
    private readonly IProtocolValidator _protocolValidator;
    private readonly INodeStatsManager _stats;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    public int ProtocolPriority => ProtocolPriorities.P2P;
    public int MessageIdSpaceSize => ProtocolMessageIdSpaces.P2P;
    public static int MessageSpace => 0x10;

    public P2PProtocolHandlerFactory(
        IRlpxHost rlpxHost,
        IMessageSerializationService serializer,
        INetworkConfig networkConfig,
        IDiscoveryApp discoveryApp,
        IProtocolValidator protocolValidator,
        INodeStatsManager stats,
        ILogManager logManager
    )
    {
        _discoveryApp = discoveryApp;
        _protocolValidator = protocolValidator;
        _stats = stats;

        if (networkConfig.ClientIdMatcher is not null)
        {
            _clientIdPattern = new Regex(networkConfig.ClientIdMatcher, RegexOptions.Compiled);
        }

        _rlpxHost = rlpxHost;
        _serializer = serializer;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<P2PProtocolHandlerFactory>();
    }

    public IProtocolHandler Create(ISession session)
    {
        P2PProtocolHandler handler = new(session, _rlpxHost.LocalNodeId, _stats, _serializer, _clientIdPattern, _logManager);
        InitP2PProtocol(session, handler);
        return handler;
    }

    private void InitP2PProtocol(ISession session, P2PProtocolHandler handler)
    {
        session.PingSender = handler;
        handler.ProtocolInitialized += (sender, args) =>
        {
            P2PProtocolInitializedEventArgs typedArgs = (P2PProtocolInitializedEventArgs)args;
            if (!RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;

            if (handler.ProtocolVersion >= 5)
            {
                if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - enabling snappy");
                session.EnableSnappy();
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - disabling snappy");
            }

            _stats.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
            {
                ClientId = typedArgs.ClientId,
                Capabilities = typedArgs.Capabilities.ToArray(),
                P2PVersion = typedArgs.P2PVersion,
                ListenPort = typedArgs.ListenPort
            });

            AddNodeToDiscovery(session, typedArgs);

            _protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, args);
        };
    }

    /// <summary>
    /// In case of IN connection we don't know what is the port node is listening on until we receive the Hello message
    /// </summary>
    private void AddNodeToDiscovery(ISession session, P2PProtocolInitializedEventArgs eventArgs)
    {
        if (eventArgs.ListenPort == 0)
        {
            if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {session}");
            return;
        }

        if (session.Node.Port != eventArgs.ListenPort)
        {
            if (_logger.IsDebug) _logger.Debug($"Updating listen port for {session:s} to: {eventArgs.ListenPort}");
            session.Node.Port = eventArgs.ListenPort;
        }

        //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
        _discoveryApp.AddNodeToDiscovery(session.Node);
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
