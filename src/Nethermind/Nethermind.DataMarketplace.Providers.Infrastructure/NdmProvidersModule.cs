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
using MongoDB.Driver;
using Nethermind.Core;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Providers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Infrastructure.Rpc;
using Nethermind.DataMarketplace.Providers.Policies;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.DataMarketplace.Providers.Validators;
using Nethermind.JsonRpc.Modules;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Infrastructure.Updaters;
using Nethermind.Sockets;
using System.Threading.Tasks;
using Nethermind.Db;
using Nethermind.Api;

namespace Nethermind.DataMarketplace.Providers.Infrastructure
{
    public class NdmProvidersModule : INdmModule
    {
        private readonly INdmApi _api;   
        private IProviderService? _providerService;
        private IReportService? _reportService; 
        private IProviderTransactionsService? _providerTransactionsService;
        private IProviderGasLimitsService? _providerGasLimitsService;
        private IGasPriceService? _gasPriceService;
        private IProviderThresholdsService? _providerThresholdsService;
        private IDepositManager? _depositManager;

        public NdmProvidersModule(INdmApi api)
        {
           _api = api ?? throw new ArgumentNullException(nameof(api)); 
        }

        public async Task InitAsync()
        {
            AddDecoders();
            var logManager = _api.LogManager;
            var logger = logManager.GetClassLogger();
            var skipDepositVerification = HasEnabledVariable("SKIP_DEPOSIT_VERIFICATION");
            var backgroundServicesDisabled = HasEnabledVariable("BACKGROUND_SERVICES_DISABLED");
            var disableSendingPaymentClaimTransaction = HasEnabledVariable("SENDING_PAYMENT_CLAIM_TRANSACTION_DISABLED");
            var instantPaymentClaimVerificationEnabled = HasEnabledVariable("INSTANT_PAYMENT_CLAIM_VERIFICATION_ENABLED");
            if (skipDepositVerification)
            {
                if (logger.IsWarn) logger.Warn("*** NDM provider skipping deposit verification ***");
            }

            if (backgroundServicesDisabled)
            {
                if (logger.IsWarn) logger.Warn("*** NDM provider background services are disabled ***");
            }
            
            if (disableSendingPaymentClaimTransaction)
            {
                if (logger.IsWarn) logger.Warn("*** NDM provider sending payment claim transaction is disabled ***");
            }
            
            if (instantPaymentClaimVerificationEnabled)
            {
                if (logger.IsWarn) logger.Warn("*** NDM provider instant payment claim verification is enabled ***");
            }
            
            var blockchainBridge = _api.BlockchainBridge;
            var ndmConfig = _api.NdmConfig;
            var txPool = _api.TxPool;
            var dbConfig = _api.ConfigProvider.GetConfig<IProviderDbConfig>();
            var contractAddress = string.IsNullOrWhiteSpace(ndmConfig.ContractAddress)
                ? Address.Zero
                : new Address(ndmConfig.ContractAddress);
            _gasPriceService = _api.GasPriceService;
            var dbInitializer = new ProviderDbInitializer(_api.DbProvider, dbConfig, _api.RocksDbFactory, _api.MemDbFactory);
            await dbInitializer.Init();
            var consumerRlpDecoder = new ConsumerDecoder();
            var dataAssetRlpDecoder = new DataAssetDecoder();
            var depositApprovalRlpDecoder = new DepositApprovalDecoder();
            var paymentClaimRlpDecoder = new PaymentClaimDecoder();
            var receiptRlpDecoder = new DataDeliveryReceiptDetailsDecoder();
            var sessionRlpDecoder = new ProviderSessionDecoder();
            var unitsRangeRlpDecoder = new UnitsRangeDecoder();
            var accountAddress = string.IsNullOrWhiteSpace(ndmConfig.ProviderAddress)
                ? Address.Zero
                : new Address(ndmConfig.ProviderAddress);
            var coldWalletAddress = string.IsNullOrWhiteSpace(ndmConfig.ProviderColdWalletAddress)
                ? Address.Zero
                : new Address(ndmConfig.ProviderColdWalletAddress);
            var depositService = new DepositService(blockchainBridge, _api.AbiEncoder,
                _api.Wallet, contractAddress);
            var paymentService = new PaymentService(blockchainBridge, _api.AbiEncoder,
                _api.Wallet, contractAddress, logManager, txPool);
            var refundPolicy = new RefundPolicy();
            var dataAvailabilityValidator = new DataAvailabilityValidator();
            var transactionVerifier = new TransactionVerifier(blockchainBridge, ndmConfig.BlockConfirmations);
            var transactionService = _api.TransactionService;
            var timestamper = _api.Timestamper;

            IConsumerRepository consumerRepository;
            IDataAssetRepository dataAssetRepository;
            IProviderDepositApprovalRepository depositApprovalRepository;
            IPaymentClaimRepository paymentClaimRepository;
            IProviderSessionRepository sessionRepository;
            IReceiptRepository receiptRepository;
            switch (ndmConfig.Persistence?.ToLowerInvariant())
            {
                case "mongo":
                    IMongoDatabase? database = _api.MongoProvider.GetDatabase();
                    if (database == null)
                    {
                        throw new InvalidOperationException("Failed to initialize Mongo DB for NDM");
                    }
                    
                    consumerRepository = new ConsumerMongoRepository(database);
                    dataAssetRepository = new DataAssetMongoRepository(database);
                    depositApprovalRepository = new ProviderDepositApprovalMongoRepository(database);
                    paymentClaimRepository = new PaymentClaimMongoRepository(database);
                    receiptRepository = new ReceiptMongoRepository(database, "providerReceipts");
                    sessionRepository = new ProviderSessionMongoRepository(database);
                    break;
                default:
                    consumerRepository = new ConsumerRocksRepository(_api.Db<IDb>(ProviderDbNames.Consumers),
                        consumerRlpDecoder);
                    dataAssetRepository = new DataAssetRocksRepository(_api.Db<IDb>(ProviderDbNames.DataAssets),
                        dataAssetRlpDecoder);
                    depositApprovalRepository = new ProviderDepositApprovalRocksRepository(
                        _api.Db<IDb>(ProviderDbNames.ProviderDepositApprovals), depositApprovalRlpDecoder);
                    paymentClaimRepository = new PaymentClaimRocksRepository(_api.Db<IDb>(ProviderDbNames.PaymentClaims),
                        paymentClaimRlpDecoder);
                    receiptRepository = new ReceiptRocksRepository(_api.Db<IDb>(ProviderDbNames.ProviderReceipts),
                        receiptRlpDecoder);
                    sessionRepository = new ProviderSessionRocksRepository(_api.Db<IDb>(ProviderDbNames.ProviderSessions),
                        sessionRlpDecoder);
                    break;
            }

            var wallet = _api.Wallet;
            var depositHandlerFactory = new DepositNodesHandlerFactory();

            _providerThresholdsService = new ProviderThresholdsService(_api.ConfigManager, ndmConfig.Id, logManager);
            
            var receiptsPolicies = new ReceiptsPolicies(_providerThresholdsService);

            IPaymentClaimProcessor paymentClaimProcessor = new PaymentClaimProcessor(_gasPriceService,
                consumerRepository, paymentClaimRepository, paymentService, coldWalletAddress, timestamper,
                unitsRangeRlpDecoder, logManager, disableSendingPaymentClaimTransaction);

            if (instantPaymentClaimVerificationEnabled)
            {
                paymentClaimProcessor = new InstantPaymentClaimProcessor(paymentClaimProcessor, paymentClaimRepository,
                    logManager);
            }

            var receiptProcessor = new ReceiptProcessor(sessionRepository, _api.AbiEncoder,
                _api.EthereumEcdsa, logManager);

            var sessionManager = new SessionManager(sessionRepository, _api.Timestamper,
                logManager);

            _depositManager = new DepositManager(depositHandlerFactory, sessionManager, receiptsPolicies, wallet,
                accountAddress, receiptProcessor, paymentClaimProcessor, consumerRepository, paymentClaimRepository,
                receiptRepository, sessionRepository, timestamper, _gasPriceService, logManager);

            _providerService = new ProviderService(_api.ConfigManager, ndmConfig.Id,
                consumerRepository, dataAssetRepository, depositApprovalRepository, paymentClaimRepository,
                paymentClaimProcessor, sessionRepository, timestamper, _api.EthereumEcdsa,
                _api.AbiEncoder, _api.NdmDataPublisher, _gasPriceService,
                dataAvailabilityValidator, sessionManager, transactionVerifier, _depositManager, refundPolicy,
                depositService, wallet, blockchainBridge, accountAddress, coldWalletAddress,
                _api.Enode.PublicKey, ndmConfig.ProviderName, ndmConfig.FilesPath,
                ndmConfig.FileMaxSize, ndmConfig.BlockConfirmations, paymentService.GasLimit, logManager,
                skipDepositVerification, backgroundServicesDisabled);

            _reportService = new ReportService(consumerRepository, paymentClaimRepository);

            _providerTransactionsService = new ProviderTransactionsService(transactionService,
                paymentClaimRepository, timestamper, logManager);

            _providerGasLimitsService = new ProviderGasLimitsService(paymentService);
            
            IWebSocketsModule ndmWebSocketsModule = _api.WebSocketsManager.GetModule("ndm"); 
            _api.NdmAccountUpdater = ndmConfig.ProviderColdWalletAddress != null
                                                                        ? new NdmAccountUpdater(ndmWebSocketsModule, _providerService.GetAddress(), _api.MainBlockProcessor, _api.StateProvider, new Address(ndmConfig.ProviderColdWalletAddress))
                                                                        : new NdmAccountUpdater(ndmWebSocketsModule ,_providerService.GetAddress(), _api.MainBlockProcessor, _api.StateProvider);
        }

        public void InitRpcModule()
        {
            _api.RpcModuleProvider.Register(new SingletonModulePool<INdmRpcProviderModule>(
                            new NdmRpcProviderModule(_providerService, _reportService, _providerTransactionsService,
                                _providerGasLimitsService, _gasPriceService, _providerThresholdsService, _depositManager),
                            true));
        }

        public IProviderService GetProviderService()
        {
            if(_providerService == null)
            {
                throw new NullReferenceException("Provider service does not exist - remember to initialize module first");
            }

            return _providerService;
        }
    

        private static void AddDecoders()
        {
            ConsumerDecoder.Init();
            PaymentClaimDecoder.Init();
            ProviderSessionDecoder.Init();
        }
        
        private static bool HasEnabledVariable(string name)
            => Environment.GetEnvironmentVariable($"NDM_PROVIDER_{name.ToUpperInvariant()}")?.ToLowerInvariant() is "true";
    }
}
