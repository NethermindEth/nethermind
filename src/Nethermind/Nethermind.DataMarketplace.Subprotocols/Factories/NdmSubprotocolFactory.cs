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
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Subprotocols.Factories
{
    public class NdmSubprotocolFactory : INdmSubprotocolFactory
    {
        protected readonly IMessageSerializationService MessageSerializationService;
        protected readonly INodeStatsManager NodeStatsManager;
        protected readonly ILogManager LogManager;
        protected readonly IAccountService AccountService;
        protected readonly IConsumerService ConsumerService;
        protected readonly INdmConsumerChannelManager NdmConsumerChannelManager;
        protected readonly IEcdsa Ecdsa;
        protected readonly IWallet Wallet;
        protected readonly INdmFaucet Faucet;
        protected readonly bool VerifySignature;
        protected readonly PublicKey NodeId;
        protected Address ProviderAddress;
        protected Address ConsumerAddress;

        public NdmSubprotocolFactory(IMessageSerializationService messageSerializationService,
            INodeStatsManager nodeStatsManager, ILogManager logManager, IAccountService accountService,
            IConsumerService consumerService, INdmConsumerChannelManager ndmConsumerChannelManager, IEcdsa ecdsa,
            IWallet wallet, INdmFaucet faucet, PublicKey nodeId, Address providerAddress, Address consumerAddress,
            bool verifySignature = true)
        {
            MessageSerializationService = messageSerializationService;
            NodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            LogManager = logManager;
            AccountService = accountService;
            ConsumerService = consumerService;
            NdmConsumerChannelManager = ndmConsumerChannelManager;
            Ecdsa = ecdsa;
            Wallet = wallet;
            Faucet = faucet;
            NodeId = nodeId;
            ProviderAddress = providerAddress;
            ConsumerAddress = consumerAddress;
            VerifySignature = verifySignature;
            AccountService.AddressChanged += (_, e) => ConsumerAddress = e.NewAddress;
        }

        public virtual INdmSubprotocol Create(ISession p2PSession)
            => new NdmSubprotocol(p2PSession, NodeStatsManager, MessageSerializationService, LogManager,
                ConsumerService, NdmConsumerChannelManager, Ecdsa, Wallet, Faucet, NodeId, ProviderAddress,
                ConsumerAddress, VerifySignature);
    }
}