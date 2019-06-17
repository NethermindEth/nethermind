using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc;
using Nethermind.DataMarketplace.Consumers.Repositories;
using Nethermind.DataMarketplace.Consumers.Services;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Db.Config;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    public static class NdmConsumersModule
    {
        public static IServices AddConsumersModule(this NdmModule.IServices services)
        {
            AddDecoders();
            var ndmConfig = services.RequiredServices.NdmConfig;
            var dbConfig = services.RequiredServices.ConfigProvider.GetConfig<IDbConfig>();
            var logManager = services.RequiredServices.LogManager;
            var rocksDbProvider = new ConsumerRocksDbProvider(services.RequiredServices.BaseDbPath, dbConfig,
                logManager);
            var depositDetailsRlpDecoder = new DepositDetailsDecoder();
            var depositApprovalRlpDecoder = new DepositApprovalDecoder();
            var receiptRlpDecoder = new DataDeliveryReceiptDetailsDecoder();
            var sessionRlpDecoder = new ConsumerSessionDecoder();
            var receiptRequestValidator = new ReceiptRequestValidator(logManager);
            var refundService = new RefundService(services.CreatedServices.BlockchainBridge,
                services.CreatedServices.AbiEncoder, services.RequiredServices.Wallet, ndmConfig, logManager);

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

            var depositService = new DepositService(services.CreatedServices.BlockchainBridge,
                services.CreatedServices.AbiEncoder, services.RequiredServices.Wallet, ndmConfig, logManager);
            var consumerService = new ConsumerService(services.RequiredServices.ConfigManager, ndmConfig.Id,
                depositRepository, depositApprovalRepository, providerRepository, receiptRepository, sessionRepository,
                services.RequiredServices.Wallet, services.CreatedServices.AbiEncoder,
                services.RequiredServices.CryptoRandom, depositService, receiptRequestValidator, refundService,
                services.CreatedServices.BlockchainBridge, services.CreatedServices.ConsumerAddress,
                services.RequiredServices.Enode.PublicKey, services.RequiredServices.Timestamp,
                ndmConfig.BlockConfirmations, logManager);
            var reportService = new ReportService(depositRepository, receiptRepository, sessionRepository,
                services.RequiredServices.Timestamp);

            services.RequiredServices.RpcModuleProvider.Register<INdmRpcConsumerModule>(
                new NdmRpcConsumerModule(consumerService, reportService,
                    services.CreatedServices.JsonRpcNdmConsumerChannel, services.RequiredServices.EthRequestService,
                    logManager));

            return new Services(consumerService);
        }

        private static void AddDecoders()
        {
            ConsumerSessionDecoder.Init();
            DepositDetailsDecoder.Init();
        }

        public interface IServices
        {
            IConsumerService ConsumerService { get; }
        }

        private class Services : IServices
        {
            public IConsumerService ConsumerService { get; }

            public Services(IConsumerService consumerService)
            {
                ConsumerService = consumerService;
            }
        }
    }
}