// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
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
        protected Address? ProviderAddress;
        protected Address? ConsumerAddress;

        public NdmSubprotocolFactory(
            IMessageSerializationService? messageSerializationService,
            INodeStatsManager? nodeStatsManager,
            ILogManager? logManager,
            IAccountService? accountService,
            IConsumerService? consumerService,
            INdmConsumerChannelManager? ndmConsumerChannelManager,
            IEcdsa? ecdsa,
            IWallet? wallet,
            INdmFaucet? faucet,
            PublicKey? nodeId,
            Address? providerAddress,
            Address? consumerAddress,
            bool verifySignature = true)
        {
            if (nodeStatsManager == null) throw new ArgumentNullException(nameof(nodeStatsManager));
            MessageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            NodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            LogManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            AccountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
            ConsumerService = consumerService ?? throw new ArgumentNullException(nameof(consumerService));
            NdmConsumerChannelManager = ndmConsumerChannelManager ?? throw new ArgumentNullException(nameof(ndmConsumerChannelManager));
            Ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            Wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            Faucet = faucet ?? throw new ArgumentNullException(nameof(faucet));
            NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            ProviderAddress = providerAddress;
            ConsumerAddress = consumerAddress;
            VerifySignature = verifySignature;
            AccountService.AddressChanged += (_, e) => ConsumerAddress = e.NewAddress;
        }

        public virtual IProtocolHandler Create(ISession p2PSession)
            => new NdmSubprotocol(p2PSession, NodeStatsManager, MessageSerializationService, LogManager,
                ConsumerService, NdmConsumerChannelManager, Ecdsa, Wallet, Faucet, NodeId, ProviderAddress,
                ConsumerAddress, VerifySignature);
    }
}
