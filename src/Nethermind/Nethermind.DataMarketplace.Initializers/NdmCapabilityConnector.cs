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
using Nethermind.DataMarketplace.Consumers;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmCapabilityConnector : INdmCapabilityConnector
    {
        private static readonly Capability Capability = new Capability(Protocol.Ndm, 1);
        private readonly IProtocolsManager _protocolsManager;
        private readonly IProtocolHandlerFactory _protocolHandlerFactory;
        private readonly IAccountService _accountService;
        private readonly Address _providerAddress;
        private readonly ILogger _logger;
        public bool CapabilityAdded { get; private set; }

        public NdmCapabilityConnector(IProtocolsManager protocolsManager,
            IProtocolHandlerFactory protocolHandlerFactory, IAccountService accountService,
            ILogManager logManager, Address providerAddress = null)
        {
            _protocolsManager = protocolsManager;
            _protocolHandlerFactory = protocolHandlerFactory;
            _accountService = accountService;
            _logger = logManager.GetClassLogger();
            _providerAddress = providerAddress;
        }

        public void Init()
        {
            if (_logger.IsTrace) _logger.Trace("Initializing NDM capability connector.");
            _accountService.AddressChanged += (_, e) =>
            {
                if (e.OldAddress == e.NewAddress)
                {
                    return;
                }
                
                if (!(e.OldAddress is null) && e.OldAddress != Address.Zero)
                {
                    return;
                }

                AddCapability();
            };
            _protocolsManager.AddProtocol(Protocol.Ndm, session => _protocolHandlerFactory.Create(session));
            var consumerAddress = _accountService.GetAddress();
            if ((consumerAddress is null || consumerAddress == Address.Zero) &&
                (_providerAddress is null || _providerAddress == Address.Zero))
            {
                return;
            }

            _protocolsManager.AddSupportedCapability(Capability);
            _protocolsManager.P2PProtocolInitialized += (sender, args) => { TryAddCapability(); };
            if (_logger.IsTrace) _logger.Trace("Initialized NDM capability connector.");
        }

        public void AddCapability()
        {
            _protocolsManager.AddSupportedCapability(Capability);
            TryAddCapability();
        }

        private void TryAddCapability()
        {
            if (CapabilityAdded)
            {
                return;
            }

            _protocolsManager.SendNewCapability(Capability);
            CapabilityAdded = true;
        }
    }
}