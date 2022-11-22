// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmCapabilityConnector : INdmCapabilityConnector
    {
        private static readonly Capability Capability = new(Protocol.Ndm, 1);
        private readonly IProtocolsManager _protocolsManager;
        private readonly IProtocolHandlerFactory _protocolHandlerFactory;
        private readonly IAccountService _accountService;
        private readonly Address? _providerAddress;
        private readonly ILogger _logger;
        public bool CapabilityAdded { get; private set; }

        public NdmCapabilityConnector(
            IProtocolsManager? protocolsManager,
            IProtocolHandlerFactory? protocolHandlerFactory,
            IAccountService? accountService,
            ILogManager? logManager,
            Address? providerAddress)
        {
            _protocolsManager = protocolsManager ?? throw new ArgumentNullException(nameof(protocolsManager));
            _protocolHandlerFactory = protocolHandlerFactory ?? throw new ArgumentNullException(nameof(protocolHandlerFactory));
            _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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
            Address? consumerAddress = _accountService.GetAddress();
            if ((consumerAddress is null || consumerAddress == Address.Zero) &&
                (_providerAddress is null || _providerAddress == Address.Zero))
            {
                return;
            }

            _protocolsManager.AddSupportedCapability(Capability);
            _protocolsManager.P2PProtocolInitialized += (_, _) => { TryAddCapability(); };
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
