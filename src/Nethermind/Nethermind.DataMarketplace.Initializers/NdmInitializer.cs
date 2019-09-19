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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers;
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
using Nethermind.DataMarketplace.WebSockets;
using Nethermind.Evm;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.Initializers
{
    [NdmInitializer("ndm")]
    public class NdmInitializer : INdmInitializer
    {
        private readonly INdmModule _ndmModule;
        private readonly INdmConsumersModule _ndmConsumersModule;

        public NdmInitializer(INdmModule ndmModule, INdmConsumersModule ndmConsumersModule)
        {
            _ndmModule = ndmModule;
            _ndmConsumersModule = ndmConsumersModule;
        }

        public virtual async Task<INdmCapabilityConnector> InitAsync(IConfigProvider configProvider,
            IDbProvider dbProvider, string baseDbPath, IBlockTree blockTree,
            ITxPool txPool, ISpecProvider specProvider, IReceiptStorage receiptStorage, IWallet wallet,
            IFilterStore filterStore, IFilterManager filterManager,
            ITimestamper timestamper, IEthereumEcdsa ecdsa, IRpcModuleProvider rpcModuleProvider, IKeyStore keyStore,
            IJsonSerializer jsonSerializer, ICryptoRandom cryptoRandom, IEnode enode,
            INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher, IGrpcServer grpcServer,
            INodeStatsManager nodeStatsManager, IProtocolsManager protocolsManager,
            IProtocolValidator protocolValidator, IMessageSerializationService messageSerializationService,
            bool enableUnsecuredDevWallet, IWebSocketsManager webSocketsManager, ILogManager logManager,
            IBlockProcessor blockProcessor)
        {
            var (config, services, faucet, accountService, consumerService, consumerAddress, providerAddress) =
                await PreInitAsync(configProvider, dbProvider, baseDbPath, blockTree, txPool, specProvider,
                    receiptStorage, wallet, filterStore, filterManager, timestamper, ecdsa, rpcModuleProvider, keyStore,
                    jsonSerializer, cryptoRandom, enode, consumerChannelManager, dataPublisher, grpcServer,
                    enableUnsecuredDevWallet, webSocketsManager, logManager, blockProcessor);
            if (!config.Enabled)
            {
                return default;
            }

            var subprotocolFactory = new NdmSubprotocolFactory(messageSerializationService, nodeStatsManager,
                logManager, accountService, consumerService, consumerChannelManager, ecdsa, wallet, faucet,
                enode.PublicKey, providerAddress, consumerAddress, config.VerifyP2PSignature);
            var protocolHandlerFactory = new ProtocolHandlerFactory(subprotocolFactory, protocolValidator,
                services.RequiredServices.EthRequestService, logManager);
            var capabilityConnector = new NdmCapabilityConnector(protocolsManager, protocolHandlerFactory,
                accountService, logManager);

            return capabilityConnector;
        }

        protected async Task<(NdmConfig config, INdmServices services, INdmFaucet faucet, IAccountService accountService,
                IConsumerService consumerService, Address consumerAddress, Address providerAddress)>
            PreInitAsync(IConfigProvider configProvider, IDbProvider dbProvider, string baseDbPath,
                IBlockTree blockTree, ITxPool txPool, ISpecProvider specProvider,
                IReceiptStorage receiptStorage, IWallet wallet, IFilterStore filterStore, IFilterManager filterManager,
                ITimestamper timestamper, IEthereumEcdsa ecdsa, IRpcModuleProvider rpcModuleProvider,
                IKeyStore keyStore, IJsonSerializer jsonSerializer, ICryptoRandom cryptoRandom, IEnode enode,
                INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher,
                IGrpcServer grpcServer, bool enableUnsecuredDevWallet, IWebSocketsManager webSocketsManager,
                ILogManager logManager, IBlockProcessor blockProcessor)
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
            var services = _ndmModule.Init(new NdmRequiredServices(configProvider, configManager, ndmConfig,
                baseDbPath, dbProvider, mongoProvider, logManager, blockTree, txPool, specProvider, receiptStorage,
                filterStore, filterManager, wallet, timestamper, ecdsa, keyStore, rpcModuleProvider, jsonSerializer,
                cryptoRandom, enode, consumerChannelManager, dataPublisher, grpcServer, ethRequestService, notifier,
                enableUnsecuredDevWallet, blockProcessor));

            var faucetAddress = string.IsNullOrWhiteSpace(ndmConfig.FaucetAddress)
                ? null
                : new Address(ndmConfig.FaucetAddress);
            var faucet = new NdmFaucet(services.CreatedServices.BlockchainBridge, ethRequestRepository, faucetAddress,
                ndmConfig.FaucetWeiRequestMaxValue, ndmConfig.FaucetEthDailyRequestsTotalValue, ndmConfig.FaucetEnabled,
                timestamper, wallet, logManager);

            var consumerAddress = string.IsNullOrWhiteSpace(ndmConfig.ConsumerAddress)
                ? Address.Zero
                : new Address(ndmConfig.ConsumerAddress);
            var providerAddress = string.IsNullOrWhiteSpace(ndmConfig.ProviderAddress)
                ? Address.Zero
                : new Address(ndmConfig.ProviderAddress);
            var consumers = _ndmConsumersModule.Init(services);

            return (ndmConfig, services, faucet, consumers.AccountService, consumers.ConsumerService, consumerAddress,
                providerAddress);
        }
    }
}