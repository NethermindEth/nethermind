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
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Notifiers;
using Nethermind.DataMarketplace.Subprotocols.Factories;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;
using Nethermind.WebSockets;

[assembly:InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
namespace Nethermind.DataMarketplace.Initializers
{
    [NdmInitializer("ndm")]
    public class NdmInitializer : INdmInitializer
    {
        private readonly INdmModule _ndmModule;
        private readonly INdmConsumersModule _ndmConsumersModule;
        
        internal string DbPath { get; private set; }

        public NdmInitializer(INdmModule ndmModule, INdmConsumersModule ndmConsumersModule)
        {
            _ndmModule = ndmModule;
            _ndmConsumersModule = ndmConsumersModule;
        }
        
        public virtual async Task<INdmCapabilityConnector> InitAsync(IConfigProvider configProvider,
            IDbProvider dbProvider, string baseDbPath, IBlockTree blockTree, ITxPool txPool, ISpecProvider specProvider,
            IReceiptStorage receiptStorage, IWallet wallet, IFilterStore filterStore, IFilterManager filterManager,
            ITimestamper timestamper, IEthereumEcdsa ecdsa, IRpcModuleProvider rpcModuleProvider, IKeyStore keyStore,
            IJsonSerializer jsonSerializer, ICryptoRandom cryptoRandom, IEnode enode,
            INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher, IGrpcServer grpcServer,
            INodeStatsManager nodeStatsManager, IProtocolsManager protocolsManager,
            IProtocolValidator protocolValidator, IMessageSerializationService messageSerializationService,
            bool enableUnsecuredDevWallet, IWebSocketsManager webSocketsManager, ILogManager logManager,
            IBlockProcessor blockProcessor, IJsonRpcClientProxy jsonRpcClientProxy,
            IEthJsonRpcClientProxy ethJsonRpcClientProxy, IHttpClient httpClient, IMonitoringService monitoringService)
        {
            var (config, services, faucet, ethRequestService, accountService, consumerService, consumerAddress,
                providerAddress) = await PreInitAsync(configProvider, dbProvider, baseDbPath, blockTree, txPool,
                specProvider, receiptStorage, wallet, filterStore, filterManager, timestamper, ecdsa, rpcModuleProvider,
                keyStore, jsonSerializer, cryptoRandom, enode, consumerChannelManager, dataPublisher, grpcServer,
                enableUnsecuredDevWallet, webSocketsManager, logManager, blockProcessor, jsonRpcClientProxy,
                ethJsonRpcClientProxy, httpClient, monitoringService);
            if (!config.Enabled)
            {
                return default;
            }

            var subprotocolFactory = new NdmSubprotocolFactory(messageSerializationService, nodeStatsManager,
                logManager, accountService, consumerService, consumerChannelManager, ecdsa, wallet, faucet,
                enode.PublicKey, providerAddress, consumerAddress, config.VerifyP2PSignature);
            var protocolHandlerFactory = new ProtocolHandlerFactory(subprotocolFactory, protocolValidator,
                ethRequestService, logManager);
            var capabilityConnector = new NdmCapabilityConnector(protocolsManager, protocolHandlerFactory,
                accountService, logManager);

            return capabilityConnector;
        }

        protected async Task<(NdmConfig config, INdmServices services, INdmFaucet faucet,
                IEthRequestService ethRequestService, IAccountService accountService,
                IConsumerService consumerService, Address consumerAddress, Address providerAddress)>
            PreInitAsync(IConfigProvider configProvider, IDbProvider dbProvider, string baseDbPath,
                IBlockTree blockTree, ITxPool txPool, ISpecProvider specProvider,
                IReceiptStorage receiptStorage, IWallet wallet, IFilterStore filterStore, IFilterManager filterManager,
                ITimestamper timestamper, IEthereumEcdsa ecdsa, IRpcModuleProvider rpcModuleProvider,
                IKeyStore keyStore, IJsonSerializer jsonSerializer, ICryptoRandom cryptoRandom, IEnode enode,
                INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher,
                IGrpcServer grpcServer, bool enableUnsecuredDevWallet, IWebSocketsManager webSocketsManager,
                ILogManager logManager, IBlockProcessor blockProcessor, IJsonRpcClientProxy jsonRpcClientProxy,
                IEthJsonRpcClientProxy ethJsonRpcClientProxy, IHttpClient httpClient,
                IMonitoringService monitoringService)
        {
            if (!(configProvider.GetConfig<INdmConfig>() is NdmConfig defaultConfig))
            {
                return default;
            }

            if (!defaultConfig.Enabled)
            {
                return default;
            }

            if (defaultConfig.StoreConfigInDatabase && string.IsNullOrWhiteSpace(defaultConfig.Id))
            {
                throw new ArgumentException("NDM config stored in database requires an id.", nameof(defaultConfig.Id));
            }

            IMongoProvider mongoProvider = null;
            IConfigRepository configRepository = null;
            IEthRequestRepository ethRequestRepository = null;
            switch (defaultConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    mongoProvider = new MongoProvider(configProvider.GetConfig<INdmMongoConfig>(), logManager);
                    var database = mongoProvider.GetDatabase();
                    configRepository = new ConfigMongoRepository(database);
                    ethRequestRepository = new EthRequestMongoRepository(database);
                    break;
                default:
                    configRepository = new ConfigRocksRepository(dbProvider.ConfigsDb, new NdmConfigDecoder());
                    ethRequestRepository = new EthRequestRocksRepository(dbProvider.EthRequestsDb,
                        new EthRequestDecoder());
                    break;
            }

            var configManager = new ConfigManager(defaultConfig, configRepository);
            var ndmConfig = await configManager.GetAsync(defaultConfig.Id);
            if (ndmConfig is null)
            {
                ndmConfig = defaultConfig;
                if (defaultConfig.StoreConfigInDatabase)
                {
                    await configManager.UpdateAsync(ndmConfig);
                }
            }

            var webSocketsModule = webSocketsManager.GetModule("ndm");
            var notifier = new NdmNotifier(webSocketsModule);
            var ethRequestService = new EthRequestService(ndmConfig.FaucetHost, logManager);
            DbPath = Path.Combine(baseDbPath, ndmConfig.DatabasePath);
            var services = _ndmModule.Init(new NdmRequiredServices(configProvider, configManager, ndmConfig,
                DbPath, dbProvider, mongoProvider, logManager, blockTree, txPool, specProvider, receiptStorage,
                filterStore, filterManager, wallet, timestamper, ecdsa, keyStore, rpcModuleProvider, jsonSerializer,
                cryptoRandom, enode, consumerChannelManager, dataPublisher, grpcServer, ethRequestService, notifier,
                enableUnsecuredDevWallet, blockProcessor, jsonRpcClientProxy, ethJsonRpcClientProxy, httpClient,
                monitoringService));

            var faucetAddress = string.IsNullOrWhiteSpace(ndmConfig.FaucetAddress)
                ? null
                : new Address(ndmConfig.FaucetAddress);

            INdmFaucet faucet;
            if (ndmConfig.FaucetEnabled)
            {
                faucet = new NdmFaucet(services.CreatedServices.BlockchainBridge, ethRequestRepository, faucetAddress,
                    ndmConfig.FaucetWeiRequestMaxValue, ndmConfig.FaucetEthDailyRequestsTotalValue,
                    ndmConfig.FaucetEnabled, timestamper, wallet, logManager);
            }
            else
            {
                faucet = new EmptyFaucet();
            }

            var consumerAddress = string.IsNullOrWhiteSpace(ndmConfig.ConsumerAddress)
                ? Address.Zero
                : new Address(ndmConfig.ConsumerAddress);
            var providerAddress = string.IsNullOrWhiteSpace(ndmConfig.ProviderAddress)
                ? Address.Zero
                : new Address(ndmConfig.ProviderAddress);
            var consumers = _ndmConsumersModule.Init(services);

            return (ndmConfig, services, faucet, ethRequestService, consumers.AccountService, consumers.ConsumerService,
                consumerAddress, providerAddress);
        }

        private class EmptyFaucet : INdmFaucet
        {
            private static readonly FaucetResponse Response = new FaucetResponse(FaucetRequestStatus.FaucetDisabled);

            public Task<FaucetResponse> TryRequestEthAsync(string node, Address address, UInt256 value)
                => Task.FromResult(Response);
        }
    }
}