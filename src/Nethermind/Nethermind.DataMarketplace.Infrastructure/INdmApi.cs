using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.Runner;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public interface INdmApi : INethermindApi
    {
        public ConfigManager? ConfigManager { get; set; }
        public IEthRequestService? EthRequestService { get; set; }
        public INdmFaucet? NdmFaucet { get; set; }
        public Address? ContractAddress { get; set; }
        public Address? ConsumerAddress { get; set; }
        public Address? ProviderAddress { get; set; }
        public IConsumerService ConsumerService { get; set; }
        public IAccountService AccountService { get; set; }
        public IRlpDecoder<DataAsset>? DataAssetRlpDecoder { get; set; }
        public IDepositService? DepositService { get; set; }
        public GasPriceService? GasPriceService { get; set; }
        public TransactionService? TransactionService { get; set; }
        public INdmNotifier? NdmNotifier { get; set; }
        public INdmDataPublisher? NdmDataPublisher { get; set; }
        public IJsonRpcNdmConsumerChannel? JsonRpcNdmConsumerChannel { get; set; }
        public INdmConsumerChannelManager? NdmConsumerChannelManager { get; set; }
        public INdmBlockchainBridge? BlockchainBridge { get; set; }
        public IHttpClient? HttpClient { get; set; }
        public IMongoProvider? MongoProvider { get; set; }
        public IDbProvider? RocksProvider { get; set; }
        
        // TODO: handle this override somehow (maybe override Config<> so it returns this? 
        public INdmConfig? NdmConfig { get; set; }
    }
}