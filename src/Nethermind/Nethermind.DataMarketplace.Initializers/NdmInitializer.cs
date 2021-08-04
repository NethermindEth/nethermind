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
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Infrastructure.Notifiers;
using Nethermind.DataMarketplace.Subprotocols.Factories;
using Nethermind.Db;
using Nethermind.Sockets;
using Nethermind.DataMarketplace.Infrastructure.Updaters;
using Nethermind.DataMarketplace.Infrastructure.Database;

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

        protected async Task PreInitAsync(INdmApi ndmApi)
        {
            if (ndmApi == null) throw new ArgumentNullException(nameof(ndmApi));
            
            IDbProvider? dbProvider = ndmApi.DbProvider;
            if (dbProvider == null)
            {
                throw new ArgumentNullException(nameof(dbProvider));
            }
            
            IConfigProvider configProvider = ndmApi.ConfigProvider;
            ILogManager logManager = ndmApi.LogManager;
            
            if (!(configProvider.GetConfig<INdmConfig>() is NdmConfig defaultConfig))
            {
                return;
            }

            if (!defaultConfig.Enabled)
            {
                return;
            }

            if (defaultConfig.StoreConfigInDatabase && string.IsNullOrWhiteSpace(defaultConfig.Id))
            {
                throw new ArgumentException("NDM config stored in database requires an id.", nameof(defaultConfig.Id));
            }
            
            IConfigRepository configRepository;
            IEthRequestRepository ethRequestRepository;
            switch (defaultConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    ndmApi.MongoProvider = new MongoProvider(configProvider.GetConfig<INdmMongoConfig>(), logManager);
                    IMongoDatabase? database = ndmApi.MongoProvider.GetDatabase();
                    if (database == null)
                    {
                        throw new ApplicationException("Failed to initialize Mongo database");
                    }

                    configRepository = new ConfigMongoRepository(database);
                    ethRequestRepository = new EthRequestMongoRepository(database);
                    break;
                default:
                    ndmApi.MongoProvider = NullMongoProvider.Instance;
                    var ndmDbProvider = new NdmDbInitializer(defaultConfig, ndmApi.DbProvider, ndmApi.RocksDbFactory, ndmApi.MemDbFactory);
                    await ndmDbProvider.Init();
                    configRepository = new ConfigRocksRepository(ndmApi.DbProvider.GetDb<IDb>(NdmDbNames.Configs), new NdmConfigDecoder());
                    ethRequestRepository = new EthRequestRocksRepository(ndmApi.DbProvider.GetDb<IDb>(NdmDbNames.EthRequests),
                        new EthRequestDecoder());
                    break;
            }
            
            ndmApi.ConfigManager = new ConfigManager(defaultConfig, configRepository);
            ndmApi.NdmConfig = await ndmApi.ConfigManager.GetAsync(defaultConfig.Id);
            if (ndmApi.NdmConfig is null)
            {
                ndmApi.NdmConfig = defaultConfig;
                if (defaultConfig.StoreConfigInDatabase)
                {
                    await ndmApi.ConfigManager.UpdateAsync((NdmConfig)ndmApi.NdmConfig);
                }
            }

            IWebSocketsModule webSocketsModule = ndmApi.WebSocketsManager!.GetModule("ndm");
            ndmApi.NdmNotifier = new NdmNotifier(webSocketsModule);

            ndmApi.EthRequestService = new EthRequestService(ndmApi.NdmConfig.FaucetHost, logManager);

            string baseDbPath = configProvider.GetConfig<IInitConfig>().BaseDbPath;
            ndmApi.BaseDbPath = DbPath = Path.Combine(baseDbPath, ndmApi.NdmConfig.DatabasePath);

            await _ndmModule.InitAsync();
            
            if (ndmApi.NdmConfig.FaucetEnabled)
            {
                // faucet should be separate from NDM? but it uses MongoDB?
                // so maybe we could extract Mongo DB beyond NDM? why would it be related?
                if (string.IsNullOrWhiteSpace(ndmApi.NdmConfig.FaucetAddress))
                {
                    ndmApi.NdmFaucet = EmptyFaucet.Instance;
                    _logger.Warn("Faucet cannot be started due to missing faucet address configuration.");
                }
                else
                {
                    Address faucetAddress = new(ndmApi.NdmConfig.FaucetAddress);
                    ndmApi.NdmFaucet = new NdmFaucet(
                        ndmApi.BlockchainBridge,
                        ethRequestRepository,
                        faucetAddress,
                        ndmApi.NdmConfig.FaucetWeiRequestMaxValue,
                        ndmApi.NdmConfig.FaucetEthDailyRequestsTotalValue,
                        ndmApi.NdmConfig.FaucetEnabled,
                        ndmApi.Timestamper,
                        ndmApi.Wallet,
                        logManager);
                }
            }
            else
            {
                ndmApi.NdmFaucet = EmptyFaucet.Instance;
            }

            ndmApi.ConsumerAddress = string.IsNullOrWhiteSpace(ndmApi.NdmConfig.ConsumerAddress)
                ? Address.Zero
                : new Address(ndmApi.NdmConfig.ConsumerAddress);
            ndmApi.ProviderAddress = string.IsNullOrWhiteSpace(ndmApi.NdmConfig.ProviderAddress)
                ? Address.Zero
                : new Address(ndmApi.NdmConfig.ProviderAddress);
            
            await _ndmConsumersModule.Init();
        }

        public virtual async Task<INdmCapabilityConnector> InitAsync(INdmApi api)
        {
            _logger = api.LogManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(api.LogManager));
            INdmConfig ndmConfig = api.Config<INdmConfig>();
            if (!ndmConfig.Enabled)
            {
                // can we not even call it here? // can be step and use the subsystems
                return NullNdmCapabilityConnector.Instance;
            }
            
            await PreInitAsync(api);

            NdmSubprotocolFactory subprotocolFactory = new NdmSubprotocolFactory(
                api.MessageSerializationService,
                api.NodeStatsManager,
                api.LogManager,
                api.AccountService,
                api.ConsumerService,
                api.NdmConsumerChannelManager,
                api.EthereumEcdsa,
                api.Wallet,
                api.NdmFaucet,
                api.Enode.PublicKey,
                api.ProviderAddress,
                api.ConsumerAddress,
                api.Config<INdmConfig>().VerifyP2PSignature);

            ProtocolHandlerFactory protocolHandlerFactory = new(
                subprotocolFactory,
                api.ProtocolValidator,
                api.EthRequestService,
                api.LogManager);

            NdmCapabilityConnector capabilityConnector = new(
                api.ProtocolsManager,
                protocolHandlerFactory,
                api.AccountService,
                api.LogManager,
                ndmConfig.ProviderAddress == null ? Address.Zero : new Address(ndmConfig.ProviderAddress));

            return capabilityConnector;
        }

        public virtual void InitRpcModules()
        {
            _ndmConsumersModule.InitRpcModules();
        }
    }
}
