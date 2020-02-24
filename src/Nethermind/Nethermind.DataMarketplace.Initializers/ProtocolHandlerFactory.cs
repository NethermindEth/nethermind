//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;

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
                var ndmEventArgs = (NdmProtocolInitializedEventArgs) args;
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