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
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Consumers.Services;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols.Factories;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Initializers
{
    [NdmInitializer("ndm")]
    public class NdmInitializer : INdmInitializer
    {
        public virtual async Task<INdmCapabilityConnector> InitAsync(IConfigProvider configProvider,
            IDbProvider dbProvider, string baseDbPath, IBlockProcessor blockProcessor, IBlockTree blockTree,
            ITxPool txPool, ITxPoolInfoProvider txPoolInfoProvider, ISpecProvider specProvider,
            IReceiptStorage receiptStorage, IWallet wallet, ITimestamp timestamp, IEcdsa ecdsa,
            IRpcModuleProvider rpcModuleProvider, IKeyStore keyStore, IJsonSerializer jsonSerializer,
            ICryptoRandom cryptoRandom, IEnode enode, INdmConsumerChannelManager consumerChannelManager,
            INdmDataPublisher dataPublisher, IGrpcService grpcService, INodeStatsManager nodeStatsManager,
            IProtocolsManager protocolsManager, IProtocolValidator protocolValidator,
            IMessageSerializationService messageSerializationService, ILogManager logManager)
        {

            var (config, _, ethRequestService, faucet, consumerService, consumerAddress,
                providerAddress) = await PreInitAsync(configProvider, dbProvider, baseDbPath, blockProcessor, blockTree,
                txPool, txPoolInfoProvider, specProvider, receiptStorage, wallet, timestamp, ecdsa, rpcModuleProvider,
                keyStore, jsonSerializer, cryptoRandom, enode, consumerChannelManager, dataPublisher, grpcService,
                logManager);
            if (!config.Enabled)
            {
                return default;
            }

            var subprotocolFactory = new NdmSubprotocolFactory(messageSerializationService, nodeStatsManager,
                logManager, consumerService, consumerChannelManager, ecdsa, wallet, faucet, enode.PublicKey,
                providerAddress, consumerAddress, config.VerifyP2PSignature);
            var capabilityConnector = new NdmCapabilityConnector(protocolsManager, subprotocolFactory,
                consumerService, protocolValidator, ethRequestService, logManager);

            return capabilityConnector;
        }

        protected async Task<(NdmConfig config, NdmModule.IServices services,
            IEthRequestService ethRequestService, INdmFaucet faucet, IConsumerService consumerService,
            Address consumerAddress, Address providerAddress)> PreInitAsync(
            IConfigProvider configProvider, IDbProvider dbProvider, string baseDbPath, IBlockProcessor blockProcessor,
            IBlockTree blockTree, ITxPool txPool, ITxPoolInfoProvider txPoolInfoProvider, ISpecProvider specProvider,
            IReceiptStorage receiptStorage, IWallet wallet, ITimestamp timestamp, IEcdsa ecdsa,
            IRpcModuleProvider rpcModuleProvider, IKeyStore keyStore, IJsonSerializer jsonSerializer,
            ICryptoRandom cryptoRandom, IEnode enode, INdmConsumerChannelManager consumerChannelManager,
            INdmDataPublisher dataPublisher, IGrpcService grpcService, ILogManager logManager)
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
                    mongoProvider = new MongoProvider(configProvider.GetConfig<IMongoConfig>(), logManager);
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

            var verifyP2PSignature = ndmConfig.VerifyP2PSignature;
            var ethRequestService = new EthRequestService(ndmConfig.FaucetHost, logManager);
            var services = NdmModule.Init(new NdmModule.RequiredServices(configProvider, configManager, ndmConfig,
                baseDbPath, dbProvider, mongoProvider, logManager, blockProcessor, blockTree, txPool,
                txPoolInfoProvider, specProvider, receiptStorage, wallet, timestamp, ecdsa, keyStore,
                rpcModuleProvider, jsonSerializer, cryptoRandom, enode, consumerChannelManager,
                dataPublisher, grpcService, ethRequestService));

            var faucetAddress = string.IsNullOrWhiteSpace(ndmConfig.FaucetAddress)
                ? null
                : new Address(ndmConfig.FaucetAddress);
            var faucet = new NdmFaucet(services.CreatedServices.BlockchainBridge, ethRequestRepository, faucetAddress,
                ndmConfig.FaucetWeiRequestMaxValue, ndmConfig.FaucetEnabled, timestamp, logManager);

            var consumerAddress = string.IsNullOrWhiteSpace(ndmConfig.ConsumerAddress)
                ? Address.Zero
                : new Address(ndmConfig.ConsumerAddress);
            var providerAddress = string.IsNullOrWhiteSpace(ndmConfig.ProviderAddress)
                ? Address.Zero
                : new Address(ndmConfig.ProviderAddress);
            var consumers = services.AddConsumersModule();

            return (ndmConfig, services, ethRequestService, faucet, consumers.ConsumerService, consumerAddress,
                providerAddress);
        }
    }
}