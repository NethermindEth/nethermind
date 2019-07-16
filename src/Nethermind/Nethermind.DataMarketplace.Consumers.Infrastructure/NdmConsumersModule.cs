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

using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
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
using Nethermind.Facade;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    public class NdmConsumersModule : INdmConsumersModule
    {
        public INdmConsumerServices Init(INdmServices services)
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
            var consumerNotifier = new ConsumerNotifier(services.RequiredServices.Notifier);
            var consumerService = new ConsumerService(services.RequiredServices.ConfigManager, ndmConfig.Id,
                depositRepository, depositApprovalRepository, providerRepository, receiptRepository, sessionRepository,
                services.RequiredServices.Wallet, services.CreatedServices.AbiEncoder,
                services.RequiredServices.CryptoRandom, depositService, receiptRequestValidator, refundService,
                services.CreatedServices.BlockchainBridge, services.CreatedServices.ConsumerAddress,
                services.RequiredServices.Enode.PublicKey, services.RequiredServices.Timestamp,
                consumerNotifier, ndmConfig.BlockConfirmations, logManager);
            var reportService = new ReportService(depositRepository, receiptRepository, sessionRepository,
                services.RequiredServices.Timestamp);

            IPersonalBridge personalBridge = services.RequiredServices.EnableUnsecuredDevWallet
                ? new PersonalBridge(services.RequiredServices.Ecdsa, services.RequiredServices.Wallet)
                : null;
            services.RequiredServices.RpcModuleProvider.Register<INdmRpcConsumerModule>(
                new NdmRpcConsumerModule(consumerService, reportService,
                    services.CreatedServices.JsonRpcNdmConsumerChannel, services.RequiredServices.EthRequestService,
                    personalBridge, logManager));

            return new NdmConsumerServices(consumerService);
        }

        private static void AddDecoders()
        {
            ConsumerSessionDecoder.Init();
            DepositDetailsDecoder.Init();
        }

        private class NdmConsumerServices : INdmConsumerServices
        {
            public IConsumerService ConsumerService { get; }

            public NdmConsumerServices(IConsumerService consumerService)
            {
                ConsumerService = consumerService;
            }
        }
    }
}