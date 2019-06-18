/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Consumers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmCapabilityConnector : INdmCapabilityConnector
    {
        private static readonly Capability Capability = new Capability(Protocol.Ndm, 1);
        private readonly IProtocolsManager _protocolsManager;
        private readonly INdmSubprotocolFactory _subprotocolFactory;
        private readonly IConsumerService _consumerService;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IEthRequestService _ethRequestService;
        private readonly ILogManager _logManager;
        private readonly Address _providerAddress;
        private bool _capabilityAdded;

        public NdmCapabilityConnector(IProtocolsManager protocolsManager, INdmSubprotocolFactory subprotocolFactory,
            IConsumerService consumerService, IProtocolValidator protocolValidator,
            IEthRequestService ethRequestService, ILogManager logManager, Address providerAddress = null)
        {
            _protocolsManager = protocolsManager;
            _subprotocolFactory = subprotocolFactory;
            _consumerService = consumerService;
            _protocolValidator = protocolValidator;
            _ethRequestService = ethRequestService;
            _logManager = logManager;
            _providerAddress = providerAddress;
        }

        public void Init()
        {
            _consumerService.AddressChanged += (_, e) =>
            {
                if (!(e.OldAddress is null) && e.OldAddress != Address.Zero)
                {
                    return;
                }

                AddCapability();
            };
            _protocolsManager.AddProtocol(Protocol.Ndm, session =>
            {
                var logger = _logManager.GetClassLogger<ProtocolsManager>();
                var handler = _subprotocolFactory.Create(session);
                handler.ProtocolInitialized += (sender, args) =>
                {
                    var ndmEventArgs = (NdmProtocolInitializedEventArgs) args;

                    _protocolValidator.DisconnectOnInvalid(Protocol.Ndm, session, ndmEventArgs);
                    if (logger.IsTrace) logger.Trace($"NDM version {handler.ProtocolVersion}: {session.RemoteNodeId}, host: {session.Node.Host}");
                    if (string.IsNullOrWhiteSpace(_ethRequestService.FaucetHost) ||
                        !session.Node.Host.Contains(_ethRequestService.FaucetHost))
                    {
                        return;
                    }

                    _ethRequestService.UpdateFaucet(handler as INdmPeer);
                };

                return handler;
            });

            var consumerAddress = _consumerService.GetAddress();
            if ((consumerAddress is null || consumerAddress == Address.Zero) &&
                (_providerAddress is null || _providerAddress == Address.Zero))
            {
                return;
            }

            _protocolsManager.AddSupportedCapability(Capability);
            _protocolsManager.P2PProtocolInitialized += (sender, args) => { TryAddCapability(); };
        }

        public void AddCapability()
        {
            _protocolsManager.AddSupportedCapability(Capability);
            TryAddCapability();
        }

        private void TryAddCapability()
        {
            if (_capabilityAdded)
            {
                return;
            }

            _protocolsManager.SendNewCapability(Capability);
            _capabilityAdded = true;
        }
    }
}