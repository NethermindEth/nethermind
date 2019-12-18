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
using Nethermind.Core;
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
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Db.Config;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.Monitoring;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    public class NdmConsumersModule : INdmConsumersModule
    {
        public INdmConsumerServices Init(INdmServices services)
        {
            AddDecoders();
            var logManager = services.RequiredServices.LogManager;
            var logger = logManager.GetClassLogger();
            
            var disableSendingDepositTransaction = HasEnabledVariable("SENDING_DEPOSIT_TRANSACTION_DISABLED");
            var instantDepositVerificationEnabled = HasEnabledVariable("INSTANT_DEPOSIT_VERIFICATION_ENABLED");
            var backgroundServicesDisabled = HasEnabledVariable("BACKGROUND_SERVICES_DISABLED");
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

            var ndmConfig = services.RequiredServices.NdmConfig;
            var configId = ndmConfig.Id;
            var dbConfig = services.RequiredServices.ConfigProvider.GetConfig<IDbConfig>();
            var contractAddress = string.IsNullOrWhiteSpace(ndmConfig.ContractAddress)
                ? Address.Zero
                : new Address(ndmConfig.ContractAddress);
            var rocksDbProvider = new ConsumerRocksDbProvider(services.RequiredServices.BaseDbPath, dbConfig,
                logManager);
            var depositDetailsRlpDecoder = new DepositDetailsDecoder();
            var depositApprovalRlpDecoder = new DepositApprovalDecoder();
            var receiptRlpDecoder = new DataDeliveryReceiptDetailsDecoder();
            var sessionRlpDecoder = new ConsumerSessionDecoder();
            var receiptRequestValidator = new ReceiptRequestValidator(logManager);

            IDepositDetailsRepository depositRepository;
            IConsumerDepositApprovalRepository depositApprovalRepository;
            IProviderRepository providerRepository;
            IReceiptRepository receiptRepository;
            IConsumerSessionRepository sessionRepository;
            switch (ndmConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    var database = services.RequiredServices.MongoProvider.GetDatabase();
                    depositRepository = new DepositDetailsMongoRepository(database);
                    depositApprovalRepository = new ConsumerDepositApprovalMongoRepository(database);
                    providerRepository = new ProviderMongoRepository(database);
                    receiptRepository = new ReceiptMongoRepository(database, "consumerReceipts");
                    sessionRepository = new ConsumerSessionMongoRepository(database);
                    break;
                case "memory":
                    if (logger.IsWarn) logger.Warn("*** NDM is using in memory database ***");
                    var depositsDatabase = new DepositsInMemoryDb();
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

            var requiredBlockConfirmations = ndmConfig.BlockConfirmations;
            var abiEncoder = services.CreatedServices.AbiEncoder;
            var blockchainBridge = services.CreatedServices.BlockchainBridge;
            var blockProcessor = services.RequiredServices.BlockProcessor;
            var configManager = services.RequiredServices.ConfigManager;
            var consumerAddress = services.CreatedServices.ConsumerAddress;
            var cryptoRandom = services.RequiredServices.CryptoRandom;
            var depositService = services.CreatedServices.DepositService;
            var gasPriceService = services.CreatedServices.GasPriceService;
            var ecdsa = services.RequiredServices.Ecdsa;
            var ethRequestService = services.RequiredServices.EthRequestService;
            var jsonRpcNdmConsumerChannel = services.CreatedServices.JsonRpcNdmConsumerChannel;
            var ndmNotifier = services.RequiredServices.Notifier;
            var nodePublicKey = services.RequiredServices.Enode.PublicKey;
            var timestamper = services.RequiredServices.Timestamper;
            var wallet = services.RequiredServices.Wallet;
            var httpClient = services.RequiredServices.HttpClient;
            var jsonRpcClientProxy = services.RequiredServices.JsonRpcClientProxy;
            var ethJsonRpcClientProxy = services.RequiredServices.EthJsonRpcClientProxy;
            var transactionService = services.CreatedServices.TransactionService;
            var monitoringService = services.RequiredServices.MonitoringService;
            monitoringService?.RegisterMetrics(typeof(Metrics));

            var dataRequestFactory = new DataRequestFactory(wallet, nodePublicKey);
            var transactionVerifier = new TransactionVerifier(blockchainBridge, requiredBlockConfirmations);
            var depositUnitsCalculator = new DepositUnitsCalculator(sessionRepository, timestamper);
            var depositProvider = new DepositProvider(depositRepository, depositUnitsCalculator, logManager);
            var kycVerifier = new KycVerifier(depositApprovalRepository, logManager);
            var consumerNotifier = new ConsumerNotifier(ndmNotifier);

            var dataAssetService = new DataAssetService(providerRepository, consumerNotifier, logManager);
            var providerService = new ProviderService(providerRepository, consumerNotifier, logManager);
            var dataRequestService = new DataRequestService(dataRequestFactory, depositProvider, kycVerifier, wallet,
                providerService, timestamper, sessionRepository, consumerNotifier, logManager);

            var sessionService = new SessionService(providerService, depositProvider, dataAssetService,
                sessionRepository, timestamper, consumerNotifier, logManager);
            var dataConsumerService = new DataConsumerService(depositProvider, sessionService,
                consumerNotifier, timestamper, sessionRepository, logManager);
            var dataStreamService = new DataStreamService(dataAssetService, depositProvider,
                providerService, sessionService, wallet, consumerNotifier, sessionRepository, logManager);
            var depositApprovalService = new DepositApprovalService(dataAssetService, providerService,
                depositApprovalRepository, timestamper, consumerNotifier, logManager);
            var depositConfirmationService = new DepositConfirmationService(blockchainBridge, consumerNotifier,
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
            
            var depositReportService = new DepositReportService(depositRepository, receiptRepository, sessionRepository,
                timestamper);
            var receiptService = new ReceiptService(depositProvider, providerService, receiptRequestValidator,
                sessionService, timestamper, receiptRepository, sessionRepository, abiEncoder, wallet, ecdsa,
                nodePublicKey, logManager);
            var refundService = new RefundService(blockchainBridge, abiEncoder, wallet, depositRepository,
                contractAddress, logManager);
            var refundClaimant = new RefundClaimant(refundService, blockchainBridge, depositRepository,
                transactionVerifier, gasPriceService, timestamper, logManager);
            var accountService = new AccountService(configManager, dataStreamService, providerService,
                sessionService, consumerNotifier, wallet, configId, consumerAddress, logManager);
            var proxyService = new ProxyService(jsonRpcClientProxy, configManager, configId, logManager);
            var consumerService = new ConsumerService(accountService, dataAssetService, dataRequestService,
                dataConsumerService, dataStreamService, depositManager, depositApprovalService, providerService,
                receiptService, refundService, sessionService, proxyService);
            var ethPriceService = new EthPriceService(httpClient, timestamper, logManager);
            var consumerTransactionsService = new ConsumerTransactionsService(transactionService, depositRepository,
                timestamper, logManager);
            var gasLimitService = new ConsumerGasLimitsService(depositService, refundService);
            
            IPersonalBridge personalBridge = services.RequiredServices.EnableUnsecuredDevWallet
                ? new PersonalBridge(ecdsa, wallet)
                : null;
            services.RequiredServices.RpcModuleProvider.Register(
                new SingletonModulePool<INdmRpcConsumerModule>(new NdmRpcConsumerModule(consumerService,
                    depositReportService, jsonRpcNdmConsumerChannel, ethRequestService, ethPriceService,
                    gasPriceService, consumerTransactionsService, gasLimitService, personalBridge, timestamper), true));

            if (!backgroundServicesDisabled)
            {
                var useDepositTimer = ndmConfig.ProxyEnabled;
                var consumerServicesBackgroundProcessor = new ConsumerServicesBackgroundProcessor(accountService,
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