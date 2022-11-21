// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.DataMarketplace.Initializers
{
    public class ProtocolHandlerFactory : IProtocolHandlerFactory
    {
        private readonly INdmSubprotocolFactory _subprotocolFactory;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IEthRequestService _ethRequestService;
        private readonly ILogger _logger;

        public ProtocolHandlerFactory(
            INdmSubprotocolFactory subprotocolFactory,
            IProtocolValidator protocolValidator,
            IEthRequestService ethRequestService,
            ILogManager logManager)
        {
            _subprotocolFactory = subprotocolFactory ?? throw new ArgumentNullException(nameof(subprotocolFactory));
            _protocolValidator = protocolValidator ?? throw new ArgumentNullException(nameof(protocolValidator));
            _ethRequestService = ethRequestService ?? throw new ArgumentNullException(nameof(ethRequestService));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public IProtocolHandler Create(ISession session)
        {
            IProtocolHandler handler = _subprotocolFactory.Create(session);
            handler.ProtocolInitialized += (sender, args) =>
            {
                var ndmEventArgs = (NdmProtocolInitializedEventArgs)args;
                _protocolValidator.DisconnectOnInvalid(Protocol.Ndm, session, ndmEventArgs);
                if (_logger.IsTrace) _logger.Trace($"NDM version {handler.ProtocolVersion}: {session.RemoteNodeId}, host: {session.Node.Host}");
                if (string.IsNullOrWhiteSpace(_ethRequestService.FaucetHost) ||
                    !session.Node.Host.Contains(_ethRequestService.FaucetHost))
                {
                    return;
                }

                INdmPeer? peer = handler as INdmPeer;
                if (peer != null)
                {
                    _ethRequestService.UpdateFaucet(peer);
                }
                else
                {
                    _logger.Warn($"NDM handler cannot serve as faucet since it is not implementing {nameof(INdmPeer)}");
                }
            };

            return handler;
        }
    }
}
