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
using MongoDB.Driver;
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.DataAssets.Services;
using Nethermind.DataMarketplace.Consumers.DataRequests.Factories;
using Nethermind.DataMarketplace.Consumers.DataRequests.Services;
using Nethermind.DataMarketplace.Consumers.DataStreams.Services;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;
using Nethermind.DataMarketplace.Consumers.Providers.Services;
using Nethermind.DataMarketplace.Consumers.Receipts.Services;
using Nethermind.DataMarketplace.Consumers.Refunds.Services;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Consumers.Sessions.Services;
using Nethermind.DataMarketplace.Consumers.Shared.Services;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Db.Rocks.Config;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Wallet;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Infrastructure.Updaters;
using Nethermind.Sockets;
using System.Threading.Tasks;
using Nethermind.Db;
using Nethermind.Api;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    public class NdmConsumersModule : INdmConsumersModule
    {
        private readonly INdmApi _api;
        private IDepositReportService depositReportService;
        private IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel;
        private IEthRequestService ethRequestService;
        private IGasPriceService gasPriceService;
        private IConsumerTransactionsService consumerTransactionsService;
        private IConsumerGasLimitsService gasLimitsService;
        private IWallet wallet;
        private ITimestamper timestamper;
        private IPriceService priceService;

        public NdmConsumersModule(INdmApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public async Task Init()
        {
            AddDecoders();
            ILogManager logManager = _api.LogManager;
            ILogger logger = logManager.GetClassLogger();

            bool disableSendingDepositTransaction = HasEnabledVariable("SENDING_DEPOSIT_TRANSACTION_DISABLED");
            bool instantDepositVerificationEnabled = HasEnabledVariable("INSTANT_DEPOSIT_VERIFICATION_ENABLED");
            bool backgroundServicesDisabled = HasEnabledVariable("BACKGROUND_SERVICES_DISABLED");
            if (disableSendingDepositTransaction)
            {
                if (logger.IsWarn) logger.Warn("*** NDM sending deposit transaction is disabled ***");
            }

            if (instantDepositVerificationEnabled)
            {
                if (logger.IsWarn) logger.Warn("*** NDM instant deposit verification is enabled ***");
            }

            if (backgroundServicesDisabled)
            {
                if (logger.IsWarn) logger.Warn("*** NDM background services are disabled ***");
            }

            INdmConfig ndmConfig = _api.NdmConfig;
            string configId = ndmConfig.Id;
            IDbConfig dbConfig = _api.ConfigProvider.GetConfig<IDbConfig>();
            Address contractAddress = string.IsNullOrWhiteSpace(ndmConfig.ContractAddress)
                ? Address.Zero
                : new Address(ndmConfig.ContractAddress);
            DepositDetailsDecoder depositDetailsRlpDecoder = new DepositDetailsDecoder();
            DepositApprovalDecoder depositApprovalRlpDecoder = new DepositApprovalDecoder();
            DataDeliveryReceiptDetailsDecoder receiptRlpDecoder = new DataDeliveryReceiptDetailsDecoder();
            ConsumerSessionDecoder sessionRlpDecoder = new ConsumerSessionDecoder();
            ReceiptRequestValidator receiptRequestValidator = new ReceiptRequestValidator(logManager);

            IDepositDetailsRepository depositRepository;
            IConsumerDepositApprovalRepository depositApprovalRepository;
            IProviderRepository providerRepository;
            IReceiptRepository receiptRepository;
            IConsumerSessionRepository sessionRepository;
            IDepositUnitsCalculator depositUnitsCalculator;
            switch (ndmConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    IMongoDatabase? database = _api.MongoProvider.GetDatabase();
                    if (database == null)
                    {
                        throw new ApplicationException("Failed to initialize Mongo DB.");
                    }
                    
                    sessionRepository = new ConsumerSessionMongoRepository(database);
                    depositUnitsCalculator = new DepositUnitsCalculator(sessionRepository, _api.Timestamper);
                    depositRepository = new DepositDetailsMongoRepository(database, depositUnitsCalculator);
                    depositApprovalRepository = new ConsumerDepositApprovalMongoRepository(database);
                    providerRepository = new ProviderMongoRepository(database);
                    receiptRepository = new ReceiptMongoRepository(database, "consumerReceipts");
                    break;
                case "memory":
                    if (logger.IsWarn) logger.Warn("*** NDM is using in memory database ***");
                    DepositsInMemoryDb depositsDatabase = new DepositsInMemoryDb();
                    sessionRepository = new ConsumerSessionInMemoryRepository();
                    depositUnitsCalculator = new DepositUnitsCalculator(sessionRepository, _api.Timestamper);
                    depositRepository = new DepositDetailsInMemoryRepository(depositsDatabase, depositUnitsCalculator);
                    depositApprovalRepository = new ConsumerDepositApprovalInMemoryRepository();
                    providerRepository = new ProviderInMemoryRepository(depositsDatabase);
                    receiptRepository = new ReceiptInMemoryRepository();
                    break;
                default:
                    var dbInitializer = new ConsumerNdmDbInitializer(_api.DbProvider, ndmConfig, _api.RocksDbFactory, _api.MemDbFactory);
                    await dbInitializer.InitAsync();
                    sessionRepository = new ConsumerSessionRocksRepository(_api.Db<IDb>(ConsumerNdmDbNames.ConsumerSessions),
                        sessionRlpDecoder);
                    depositUnitsCalculator = new DepositUnitsCalculator(sessionRepository, _api.Timestamper);
                    depositRepository = new DepositDetailsRocksRepository(_api.Db<IDb>(ConsumerNdmDbNames.Deposits),
                        depositDetailsRlpDecoder, depositUnitsCalculator);
                    depositApprovalRepository = new ConsumerDepositApprovalRocksRepository(
                        _api.Db<IDb>(ConsumerNdmDbNames.ConsumerDepositApprovals), depositApprovalRlpDecoder);
                    providerRepository = new ProviderRocksRepository(_api.Db<IDb>(ConsumerNdmDbNames.Deposits),
                        depositDetailsRlpDecoder);
                    receiptRepository = new ReceiptRocksRepository(_api.Db<IDb>(ConsumerNdmDbNames.ConsumerReceipts),
                        receiptRlpDecoder);

                    break;
            }

            uint requiredBlockConfirmations = ndmConfig.BlockConfirmations;
            IAbiEncoder abiEncoder = _api.AbiEncoder;
            INdmBlockchainBridge blockchainBridge = _api.BlockchainBridge;
            IBlockProcessor blockProcessor = _api.MainBlockProcessor;
            IConfigManager configManager = _api.ConfigManager;
            Address consumerAddress = _api.ConsumerAddress;
            ICryptoRandom cryptoRandom = _api.CryptoRandom;
            IDepositService depositService = _api.DepositService;
            gasPriceService = _api.GasPriceService;
            IEthereumEcdsa ecdsa = _api.EthereumEcdsa;
            ethRequestService = _api.EthRequestService;
            jsonRpcNdmConsumerChannel = _api.JsonRpcNdmConsumerChannel;
            INdmNotifier ndmNotifier = _api.NdmNotifier;
            PublicKey nodePublicKey = _api.Enode.PublicKey;
            timestamper = _api.Timestamper;
            IWallet wallet = _api.Wallet;
            IHttpClient httpClient = _api.HttpClient;
            IJsonRpcClientProxy? jsonRpcClientProxy = _api.JsonRpcClientProxy;
            IEthJsonRpcClientProxy? ethJsonRpcClientProxy = _api.EthJsonRpcClientProxy;
            TransactionService transactionService = _api.TransactionService;
            IMonitoringService monitoringService = _api.MonitoringService;
            IWebSocketsModule ndmWebSocketsModule = _api.WebSocketsManager.GetModule("ndm");
            monitoringService?.RegisterMetrics(typeof(Metrics));

            DataRequestFactory dataRequestFactory = new DataRequestFactory(wallet, nodePublicKey);
            TransactionVerifier transactionVerifier = new TransactionVerifier(blockchainBridge, requiredBlockConfirmations);
            DepositProvider depositProvider = new DepositProvider(depositRepository, depositUnitsCalculator, logManager);
            KycVerifier kycVerifier = new KycVerifier(depositApprovalRepository, logManager);
            ConsumerNotifier consumerNotifier = new ConsumerNotifier(ndmNotifier);

            DataAssetService dataAssetService = new DataAssetService(providerRepository, consumerNotifier, logManager);
            ProviderService providerService = new ProviderService(providerRepository, consumerNotifier, logManager);
            DataRequestService dataRequestService = new DataRequestService(dataRequestFactory, depositProvider, kycVerifier, wallet,
                providerService, timestamper, sessionRepository, consumerNotifier, logManager);

            SessionService sessionService = new SessionService(providerService, depositProvider, dataAssetService,
                sessionRepository, timestamper, consumerNotifier, logManager);
            DataConsumerService dataConsumerService = new DataConsumerService(depositProvider, sessionService,
                consumerNotifier, timestamper, sessionRepository, logManager);
            DataStreamService dataStreamService = new DataStreamService(dataAssetService, depositProvider,
                providerService, sessionService, wallet, consumerNotifier, sessionRepository, logManager);
            DepositApprovalService depositApprovalService = new DepositApprovalService(dataAssetService, providerService,
                depositApprovalRepository, timestamper, consumerNotifier, logManager);
            DepositConfirmationService depositConfirmationService = new DepositConfirmationService(blockchainBridge, consumerNotifier,
                depositRepository, depositService, logManager, requiredBlockConfirmations);

            IDepositManager depositManager = new DepositManager(depositService, depositUnitsCalculator,
                dataAssetService, kycVerifier, providerService, abiEncoder, cryptoRandom, wallet, gasPriceService,
                depositRepository, timestamper, logManager, requiredBlockConfirmations,
                disableSendingDepositTransaction);

            if (instantDepositVerificationEnabled)
            {
                depositManager = new InstantDepositManager(depositManager, depositRepository, timestamper, logManager,
                    requiredBlockConfirmations);
            }

            depositReportService = new DepositReportService(depositRepository, depositUnitsCalculator, receiptRepository, sessionRepository,
                timestamper);
            ReceiptService receiptService = new ReceiptService(depositProvider, providerService, receiptRequestValidator,
                sessionService, timestamper, receiptRepository, sessionRepository, abiEncoder, wallet, ecdsa,
                nodePublicKey, logManager);
            RefundService refundService = new RefundService(blockchainBridge, abiEncoder, depositRepository,
                contractAddress, logManager, wallet);
            RefundClaimant refundClaimant = new RefundClaimant(refundService, blockchainBridge, depositRepository,
                transactionVerifier, gasPriceService, timestamper, logManager);
            _api.AccountService = new AccountService(configManager, dataStreamService, providerService,
                sessionService, consumerNotifier, wallet, configId, consumerAddress, logManager);
            _api.NdmAccountUpdater = new NdmAccountUpdater(ndmWebSocketsModule, consumerAddress, _api.MainBlockProcessor, _api.ChainHeadStateProvider);
            ProxyService proxyService = new ProxyService(jsonRpcClientProxy, configManager, configId, logManager);
            _api.ConsumerService = new ConsumerService(_api.AccountService, dataAssetService, dataRequestService,
                dataConsumerService, dataStreamService, depositManager, depositApprovalService, providerService,
                receiptService, refundService, sessionService, proxyService);
            priceService = new PriceService(httpClient, timestamper, logManager);
            consumerTransactionsService = new ConsumerTransactionsService(transactionService, depositRepository,
                timestamper, logManager);
            gasLimitsService = new ConsumerGasLimitsService(depositService, refundService);

            if (!backgroundServicesDisabled)
            {
                bool useDepositTimer = ndmConfig.ProxyEnabled;
                ConsumerServicesBackgroundProcessor consumerServicesBackgroundProcessor =
                    new ConsumerServicesBackgroundProcessor(
                        _api.AccountService,
                        refundClaimant,
                        depositConfirmationService,
                        _api.GasPriceService,
                        _api.MainBlockProcessor,
                        depositRepository,
                        consumerNotifier,
                        logManager,
                        priceService,
                        useDepositTimer,
                        ethJsonRpcClientProxy);
                consumerServicesBackgroundProcessor.Init();
            }
        }

        public void InitRpcModules()
        {
            _api.RpcModuleProvider.Register(new SingletonModulePool<INdmRpcConsumerModule>(
                new NdmRpcConsumerModule(_api.ConsumerService, depositReportService, jsonRpcNdmConsumerChannel,
                    ethRequestService, gasPriceService, consumerTransactionsService, gasLimitsService, _api.Wallet,
                    timestamper, priceService), true));
        }

        private static void AddDecoders()
        {
            ConsumerSessionDecoder.Init();
            DepositDetailsDecoder.Init();
        }

        private static bool HasEnabledVariable(string name)
            => Environment.GetEnvironmentVariable($"NDM_{name.ToUpperInvariant()}")?.ToLowerInvariant() is "true";
    }
}
