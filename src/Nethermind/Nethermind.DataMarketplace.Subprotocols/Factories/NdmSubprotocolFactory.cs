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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Services;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Subprotocols.Factories
{
    public class NdmSubprotocolFactory : INdmSubprotocolFactory
    {
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ILogManager _logManager;
        private readonly IConsumerService _consumerService;
        private readonly INdmConsumerChannelManager _ndmConsumerChannelManager;
        private readonly IEcdsa _ecdsa;
        private readonly IWallet _wallet;
        private Address _consumerAddress;
        private readonly INdmFaucet _faucet;
        private readonly bool _verifySignature;
        private readonly PublicKey _nodeId;
        private readonly Address _providerAddress;

        public NdmSubprotocolFactory(IMessageSerializationService messageSerializationService,
            INodeStatsManager nodeStatsManager, ILogManager logManager, IConsumerService consumerService,
            INdmConsumerChannelManager ndmConsumerChannelManager, IEcdsa ecdsa, IWallet wallet, INdmFaucet faucet,
            PublicKey nodeId, Address providerAddress, Address consumerAddress,
            bool verifySignature = true)
        {
            _messageSerializationService = messageSerializationService;
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _logManager = logManager;
            _consumerService = consumerService;
            _ndmConsumerChannelManager = ndmConsumerChannelManager;
            _ecdsa = ecdsa;
            _wallet = wallet;
            _faucet = faucet;
            _nodeId = nodeId;
            _providerAddress = providerAddress;
            _consumerAddress = consumerAddress;
            _verifySignature = verifySignature;
            _consumerService.AddressChanged += (_, e) => _consumerAddress = e.NewAddress;
        }

        public virtual INdmSubprotocol Create(ISession p2PSession)
            => new NdmSubprotocol(p2PSession, _nodeStatsManager, _messageSerializationService, _logManager,
                _consumerService, _ndmConsumerChannelManager, _ecdsa, _wallet, _faucet, _nodeId, _providerAddress,
                _consumerAddress, _verifySignature);
    }
}