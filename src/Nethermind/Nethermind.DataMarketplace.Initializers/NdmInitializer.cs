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
using MongoDB.Driver;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Notifiers;
using Nethermind.DataMarketplace.Subprotocols.Factories;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Db.Blooms;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.WebSockets;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Test")]

namespace Nethermind.DataMarketplace.Initializers
{
    [NdmInitializer("ndm")]
    public class NdmInitializer : INdmInitializer
    {
        private readonly INdmModule _ndmModule;
        private readonly INdmConsumersModule _ndmConsumersModule;
        private ILogger _logger;

        internal string? DbPath { get; private set; }

        public NdmInitializer(INdmModule ndmModule, INdmConsumersModule ndmConsumersModule, ILogManager logManager)
        {
            _ndmModule = ndmModule ?? throw new ArgumentNullException(nameof(ndmModule));
            _ndmConsumersModule = ndmConsumersModule ?? throw new ArgumentNullException(nameof(ndmConsumersModule));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public virtual async Task<INdmCapabilityConnector> InitAsync(
            IConfigProvider configProvider,
            IDbProvider dbProvider,
            string baseDbPath,
            IBlockTree blockTree,
            ITxPool txPool,
            ISpecProvider specProvider,
            IReceiptFinder receiptFinder,
            IWallet wallet,
            IFilterStore filterStore,
            IFilterManager filterManager,
            ITimestamper timestamper,
            IEthereumEcdsa ecdsa,
            IRpcModuleProvider rpcModuleProvider,
            IKeyStore keyStore,
            IJsonSerializer jsonSerializer,
            ICryptoRandom cryptoRandom,
            IEnode enode,
            INdmConsumerChannelManager consumerChannelManager,
            INdmDataPublisher dataPublisher,
            IGrpcServer grpcServer,
            INodeStatsManager nodeStatsManager,
            IProtocolsManager protocolsManager,
            IProtocolValidator protocolValidator,
            IMessageSerializationService messageSerializationService,
            bool enableUnsecuredDevWallet,
            IWebSocketsManager webSocketsManager,
            ILogManager logManager,
            IBlockProcessor blockProcessor,
            IJsonRpcClientProxy? jsonRpcClientProxy,
            IEthJsonRpcClientProxy? ethJsonRpcClientProxy,
            IHttpClient httpClient,
            IMonitoringService monitoringService,
            IBloomStorage bloomStorage)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            INdmConfig ndmConfig = configProvider.GetConfig<INdmConfig>();
            if (!ndmConfig.Enabled)
            {
                // can we not even call it here? // can be step and use the subsystems
                return NullNdmCapabilityConnector.Instance;
            }

            (NdmConfig config, INdmServices services, INdmFaucet faucet, IEthRequestService ethRequestService, IAccountService accountService, IConsumerService consumerService, Address consumerAddress, Address providerAddress) = await PreInitAsync(
                configProvider,
                dbProvider,
                baseDbPath,
                blockTree,
                txPool,
                specProvider,
                receiptFinder,
                wallet,
                filterStore,
                filterManager,
                timestamper,
                ecdsa,
                rpcModuleProvider,
                keyStore,
                jsonSerializer,
                cryptoRandom,
                enode,
                consumerChannelManager,
                dataPublisher,
                grpcServer,
                enableUnsecuredDevWallet,
                webSocketsManager,
                logManager,
                blockProcessor,
                jsonRpcClientProxy,
                ethJsonRpcClientProxy,
                httpClient,
                monitoringService,
                bloomStorage);

            NdmSubprotocolFactory subprotocolFactory = new NdmSubprotocolFactory(messageSerializationService, nodeStatsManager,
                logManager, accountService, consumerService, consumerChannelManager, ecdsa, wallet, faucet,
                enode.PublicKey, providerAddress, consumerAddress, config.VerifyP2PSignature);
            ProtocolHandlerFactory protocolHandlerFactory = new ProtocolHandlerFactory(subprotocolFactory, protocolValidator,
                ethRequestService, logManager);

            NdmCapabilityConnector capabilityConnector = new NdmCapabilityConnector(
                protocolsManager,
                protocolHandlerFactory,
                accountService,
                logManager,
                ndmConfig.ProviderAddress == null ? Address.Zero : new Address(ndmConfig.ProviderAddress));

            return capabilityConnector;
        }

        protected async Task<(NdmConfig config, INdmServices services, INdmFaucet faucet,
                IEthRequestService ethRequestService, IAccountService accountService,
                IConsumerService consumerService, Address consumerAddress, Address providerAddress)>
            PreInitAsync(
                IConfigProvider configProvider,
                IDbProvider dbProvider,
                string baseDbPath,
                IBlockTree blockTree,
                ITxPool txPool,
                ISpecProvider specProvider,
                IReceiptFinder receiptFinder,
                IWallet wallet,
                IFilterStore filterStore,
                IFilterManager filterManager,
                ITimestamper timestamper,
                IEthereumEcdsa ecdsa,
                IRpcModuleProvider rpcModuleProvider,
                IKeyStore keyStore,
                IJsonSerializer jsonSerializer,
                ICryptoRandom cryptoRandom,
                IEnode enode,
                INdmConsumerChannelManager consumerChannelManager,
                INdmDataPublisher dataPublisher,
                IGrpcServer grpcServer,
                bool enableUnsecuredDevWallet,
                IWebSocketsManager webSocketsManager,
                ILogManager logManager,
                IBlockProcessor blockProcessor,
                IJsonRpcClientProxy? jsonRpcClientProxy,
                IEthJsonRpcClientProxy? ethJsonRpcClientProxy,
                IHttpClient httpClient,
                IMonitoringService monitoringService,
                IBloomStorage bloomStorage)
        {
            // what is block processor doing here?

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

            IMongoProvider mongoProvider;
            IConfigRepository configRepository;
            IEthRequestRepository ethRequestRepository;
            switch (defaultConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    mongoProvider = new MongoProvider(configProvider.GetConfig<INdmMongoConfig>(), logManager);
                    IMongoDatabase? database = mongoProvider.GetDatabase();
                    if (database == null)
                    {
                        throw new ApplicationException("Failed to initialize Mongo database");
                    }

                    configRepository = new ConfigMongoRepository(database);
                    ethRequestRepository = new EthRequestMongoRepository(database);
                    break;
                default:
                    mongoProvider = NullMongoProvider.Instance;
                    configRepository = new ConfigRocksRepository(dbProvider.ConfigsDb, new NdmConfigDecoder());
                    ethRequestRepository = new EthRequestRocksRepository(dbProvider.EthRequestsDb,
                        new EthRequestDecoder());
                    break;
            }

            ConfigManager configManager = new ConfigManager(defaultConfig, configRepository);
            NdmConfig? ndmConfig = await configManager.GetAsync(defaultConfig.Id);
            if (ndmConfig is null)
            {
                ndmConfig = defaultConfig;
                if (defaultConfig.StoreConfigInDatabase)
                {
                    await configManager.UpdateAsync(ndmConfig);
                }
            }

            IWebSocketsModule webSocketsModule = webSocketsManager.GetModule("ndm");
            NdmNotifier notifier = new NdmNotifier(webSocketsModule);
            EthRequestService ethRequestService = new EthRequestService(ndmConfig.FaucetHost, logManager);
            DbPath = Path.Combine(baseDbPath, ndmConfig.DatabasePath);

            INdmServices services = _ndmModule.Init(
                new NdmRequiredServices(
                    configProvider,
                    configManager,
                    ndmConfig,
                    DbPath,
                    dbProvider,
                    mongoProvider,
                    logManager,
                    blockTree,
                    txPool,
                    specProvider,
                    receiptFinder,
                    filterStore,
                    filterManager,
                    wallet,
                    timestamper,
                    ecdsa,
                    keyStore,
                    rpcModuleProvider,
                    jsonSerializer,
                    cryptoRandom,
                    enode,
                    consumerChannelManager,
                    dataPublisher,
                    grpcServer,
                    ethRequestService,
                    notifier,
                    enableUnsecuredDevWallet,
                    blockProcessor,
                    jsonRpcClientProxy,
                    ethJsonRpcClientProxy,
                    httpClient,
                    monitoringService,
                    bloomStorage));

            INdmFaucet faucet;
            if (ndmConfig.FaucetEnabled)
            {
                // faucet should be separate from NDM? but it uses MongoDB?
                // so maybe we could extract Mongo DB beyond NDM? why would it be related?
                if (string.IsNullOrWhiteSpace(ndmConfig.FaucetAddress))
                {
                    faucet = EmptyFaucet.Instance;
                    _logger.Warn("Faucet cannot be started due to missing faucet address configuration.");
                }
                else
                {
                    Address faucetAddress = new Address(ndmConfig.FaucetAddress);
                    faucet = new NdmFaucet(
                        services.CreatedServices.BlockchainBridge,
                        ethRequestRepository,
                        faucetAddress,
                        ndmConfig.FaucetWeiRequestMaxValue,
                        ndmConfig.FaucetEthDailyRequestsTotalValue,
                        ndmConfig.FaucetEnabled,
                        timestamper,
                        wallet,
                        logManager);
                }
            }
            else
            {
                faucet = EmptyFaucet.Instance;
            }

            Address consumerAddress = string.IsNullOrWhiteSpace(ndmConfig.ConsumerAddress)
                ? Address.Zero
                : new Address(ndmConfig.ConsumerAddress);
            Address providerAddress = string.IsNullOrWhiteSpace(ndmConfig.ProviderAddress)
                ? Address.Zero
                : new Address(ndmConfig.ProviderAddress);
            INdmConsumerServices consumers = _ndmConsumersModule.Init(services);

            return (ndmConfig, services, faucet, ethRequestService, consumers.AccountService, consumers.ConsumerService,
                consumerAddress, providerAddress);
        }
    }
}