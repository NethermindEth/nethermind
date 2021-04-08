using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.DataMarketplace.Subprotocols.Factories;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Providers.Infrastructure
{
    internal class NdmProviderSubprotocolFactory : NdmSubprotocolFactory
    {
        private readonly IProviderService _providerService;

        public NdmProviderSubprotocolFactory(IMessageSerializationService messageSerializationService,
            INodeStatsManager nodeStatsManager, ILogManager logManager, IAccountService accountService,
            IConsumerService consumerService, IProviderService providerService,
            INdmConsumerChannelManager ndmConsumerChannelManager, IEcdsa ecdsa, IWallet wallet, INdmFaucet faucet,
            PublicKey nodeId, Address providerAddress, Address consumerAddress, bool verifySignature = true) : base(
            messageSerializationService, nodeStatsManager, logManager, accountService, consumerService,
            ndmConsumerChannelManager, ecdsa, wallet, faucet, nodeId, providerAddress, consumerAddress, verifySignature)
        {
            _providerService = providerService;
            _providerService.AddressChanged += (_, e) => ProviderAddress = e.NewAddress;
        }

        public override IProtocolHandler Create(ISession p2PSession)
            => new NdmProviderSubprotocol(p2PSession, NodeStatsManager, MessageSerializationService, LogManager,
                ConsumerService, _providerService, NdmConsumerChannelManager, Ecdsa, Wallet, Faucet, NodeId,
                ProviderAddress, ConsumerAddress, VerifySignature);
    }
}