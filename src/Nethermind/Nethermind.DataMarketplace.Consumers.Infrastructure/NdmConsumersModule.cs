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
using Nethermind.Blockchain;
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
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Consumers.Shared.Services;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Db.Rocks.Config;
using Nethermind.Facade;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    public class NdmConsumersModule : INdmConsumersModule
    {
        public INdmConsumerServices Init(INdmServices services)
        {
            AddDecoders();
            ILogManager logManager = services.RequiredServices.LogManager;
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

            INdmConfig ndmConfig = services.RequiredServices.NdmConfig;
            string configId = ndmConfig.Id;
            IDbConfig dbConfig = services.RequiredServices.ConfigProvider.GetConfig<IDbConfig>();
            Address contractAddress = string.IsNullOrWhiteSpace(ndmConfig.ContractAddress)
                ? Address.Zero
                : new Address(ndmConfig.ContractAddress);
            ConsumerRocksDbProvider rocksDbProvider = new ConsumerRocksDbProvider(services.RequiredServices.BaseDbPath, dbConfig,
                logManager);
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
            switch (ndmConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    IMongoDatabase? database = services.RequiredServices.MongoProvider.GetDatabase();
                    if (database == null)
                    {
                        throw new ApplicationException("Failed to initialize Mongo DB.");
                    }
                    
                    depositRepository = new DepositDetailsMongoRepository(database);
                    depositApprovalRepository = new ConsumerDepositApprovalMongoRepository(database);
                    providerRepository = new ProviderMongoRepository(database);
                    receiptRepository = new ReceiptMongoRepository(database, "consumerReceipts");
                    sessionRepository = new ConsumerSessionMongoRepository(database);
                    break;
                case "memory":
                    if (logger.IsWarn) logger.Warn("*** NDM is using in memory database ***");
                    DepositsInMemoryDb depositsDatabase = new DepositsInMemoryDb();
                    depositRepository = new DepositDetailsInMemoryRepository(depositsDatabase);
                    depositApprovalRepository = new ConsumerDepositApprovalInMemoryRepository();
                    providerRepository = new ProviderInMemoryRepository(depositsDatabase);
                    receiptRepository = new ReceiptInMemoryRepository();
                    sessionRepository = new ConsumerSessionInMemoryRepository();
                    break;   
                default:
                    depositRepository = new DepositDetailsRocksRepository(rocksDbProvider.DepositsDb,
                        depositDetailsRlpDecoder);
                    depositApprovalRepository = new ConsumerDepositApprovalRocksRepository(
                        rocksDbProvider.ConsumerDepositApprovalsDb, depositApprovalRlpDecoder);
                    providerRepository = new ProviderRocksRepository(rocksDbProvider.DepositsDb,
                        depositDetailsRlpDecoder);
                    receiptRepository = new ReceiptRocksRepository(rocksDbProvider.ConsumerReceiptsDb,
                        receiptRlpDecoder);
                    sessionRepository = new ConsumerSessionRocksRepository(rocksDbProvider.ConsumerSessionsDb,
                        sessionRlpDecoder);
                    break;
            }

            uint requiredBlockConfirmations = ndmConfig.BlockConfirmations;
            IAbiEncoder abiEncoder = services.CreatedServices.AbiEncoder;
            INdmBlockchainBridge blockchainBridge = services.CreatedServices.BlockchainBridge;
            IBlockProcessor blockProcessor = services.RequiredServices.BlockProcessor;
            IConfigManager configManager = services.RequiredServices.ConfigManager;
            Address consumerAddress = services.CreatedServices.ConsumerAddress;
            ICryptoRandom cryptoRandom = services.RequiredServices.CryptoRandom;
            IDepositService depositService = services.CreatedServices.DepositService;
            GasPriceService gasPriceService = services.CreatedServices.GasPriceService;
            IEthereumEcdsa ecdsa = services.RequiredServices.Ecdsa;
            IEthRequestService ethRequestService = services.RequiredServices.EthRequestService;
            IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel = services.CreatedServices.JsonRpcNdmConsumerChannel;
            INdmNotifier ndmNotifier = services.RequiredServices.Notifier;
            PublicKey nodePublicKey = services.RequiredServices.Enode.PublicKey;
            ITimestamper timestamper = services.RequiredServices.Timestamper;
            IWallet wallet = services.RequiredServices.Wallet;
            IHttpClient httpClient = services.RequiredServices.HttpClient;
            IJsonRpcClientProxy? jsonRpcClientProxy = services.RequiredServices.JsonRpcClientProxy;
            IEthJsonRpcClientProxy? ethJsonRpcClientProxy = services.RequiredServices.EthJsonRpcClientProxy;
            TransactionService transactionService = services.CreatedServices.TransactionService;
            IMonitoringService monitoringService = services.RequiredServices.MonitoringService;
            monitoringService?.RegisterMetrics(typeof(Metrics));

            DataRequestFactory dataRequestFactory = new DataRequestFactory(wallet, nodePublicKey);
            TransactionVerifier transactionVerifier = new TransactionVerifier(blockchainBridge, requiredBlockConfirmations);
            DepositUnitsCalculator depositUnitsCalculator = new DepositUnitsCalculator(sessionRepository, timestamper);
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
            
            DepositReportService depositReportService = new DepositReportService(depositRepository, receiptRepository, sessionRepository,
                timestamper);
            ReceiptService receiptService = new ReceiptService(depositProvider, providerService, receiptRequestValidator,
                sessionService, timestamper, receiptRepository, sessionRepository, abiEncoder, wallet, ecdsa,
                nodePublicKey, logManager);
            RefundService refundService = new RefundService(blockchainBridge, abiEncoder, wallet, depositRepository,
                contractAddress, logManager);
            RefundClaimant refundClaimant = new RefundClaimant(refundService, blockchainBridge, depositRepository,
                transactionVerifier, gasPriceService, timestamper, logManager);
            AccountService accountService = new AccountService(configManager, dataStreamService, providerService,
                sessionService, consumerNotifier, wallet, configId, consumerAddress, logManager);
            ProxyService proxyService = new ProxyService(jsonRpcClientProxy, configManager, configId, logManager);
            ConsumerService consumerService = new ConsumerService(accountService, dataAssetService, dataRequestService,
                dataConsumerService, dataStreamService, depositManager, depositApprovalService, providerService,
                receiptService, refundService, sessionService, proxyService);
            EthPriceService ethPriceService = new EthPriceService(httpClient, timestamper, logManager);
            ConsumerTransactionsService consumerTransactionsService = new ConsumerTransactionsService(transactionService, depositRepository,
                timestamper, logManager);
            ConsumerGasLimitsService gasLimitService = new ConsumerGasLimitsService(depositService, refundService);
            
            IPersonalBridge personalBridge = services.RequiredServices.EnableUnsecuredDevWallet
                ? new PersonalBridge(ecdsa, wallet)
                : NullPersonalBridge.Instance;
            services.RequiredServices.RpcModuleProvider.Register(
                new SingletonModulePool<INdmRpcConsumerModule>(new NdmRpcConsumerModule(consumerService,
                    depositReportService, jsonRpcNdmConsumerChannel, ethRequestService, ethPriceService,
                    gasPriceService, consumerTransactionsService, gasLimitService, personalBridge, timestamper), true));

            if (!backgroundServicesDisabled)
            {
                bool useDepositTimer = ndmConfig.ProxyEnabled;
                ConsumerServicesBackgroundProcessor consumerServicesBackgroundProcessor = new ConsumerServicesBackgroundProcessor(accountService,
                    refundClaimant, depositConfirmationService, ethPriceService, gasPriceService, blockProcessor,
                    depositRepository, consumerNotifier, logManager, useDepositTimer, ethJsonRpcClientProxy);
                consumerServicesBackgroundProcessor.Init();
            }
            
            return new NdmConsumerServices(accountService, consumerService);
        }

        private static void AddDecoders()
        {
            ConsumerSessionDecoder.Init();
            DepositDetailsDecoder.Init();
        }
        
        private static bool HasEnabledVariable(string name)
            => Environment.GetEnvironmentVariable($"NDM_{name.ToUpperInvariant()}")?.ToLowerInvariant() is "true";
        
        private class NdmConsumerServices : INdmConsumerServices
        {
            public IAccountService AccountService { get; }
            public IConsumerService ConsumerService { get; }

            public NdmConsumerServices(IAccountService accountService, IConsumerService consumerService)
            {
                AccountService = accountService;
                ConsumerService = consumerService;
            }
        }
    }
}