using Nethermind.Api;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Updaters;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public interface INdmApi : INethermindApi
    {
        IConfigManager? ConfigManager { get; set; }
        IEthRequestService? EthRequestService { get; set; }
        INdmFaucet? NdmFaucet { get; set; }
        Address? ContractAddress { get; set; }
        Address? ConsumerAddress { get; set; }
        Address? ProviderAddress { get; set; }
        IConsumerService ConsumerService { get; set; }
        IAccountService AccountService { get; set; }
        IRlpDecoder<DataAsset>? DataAssetRlpDecoder { get; set; }
        IDepositService? DepositService { get; set; }
        GasPriceService? GasPriceService { get; set; }
        TransactionService? TransactionService { get; set; }
        INdmNotifier? NdmNotifier { get; set; }
        INdmAccountUpdater NdmAccountUpdater { get; set; }
        INdmDataPublisher? NdmDataPublisher { get; set; }
        IJsonRpcNdmConsumerChannel? JsonRpcNdmConsumerChannel { get; set; }
        INdmConsumerChannelManager? NdmConsumerChannelManager { get; set; }
        INdmBlockchainBridge? BlockchainBridge { get; set; }
        IHttpClient? HttpClient { get; set; }
        IMongoProvider? MongoProvider { get; set; }
        IDbProvider? RocksProvider { get; set; }
        IEthJsonRpcClientProxy? EthJsonRpcClientProxy { get; set; } // maybe only in NDM
        IJsonRpcClientProxy? JsonRpcClientProxy { get; set; } // maybe only in NDM
        
        // TODO: handle this override somehow (maybe override Config<> so it returns this? 
        INdmConfig? NdmConfig { get; set; }
        string? BaseDbPath { get; set; }
    }
}