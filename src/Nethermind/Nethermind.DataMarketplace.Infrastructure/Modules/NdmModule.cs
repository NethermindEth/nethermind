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

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Facade;
using Nethermind.Facade.Config;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Infrastructure.Modules
{
    public class NdmModule : INdmModule
    {
        public INdmServices Init(NdmRequiredServices services)
        {
            AddDecoders();
            var config = services.NdmConfig;
            var consumerAddress = string.IsNullOrWhiteSpace(config.ConsumerAddress)
                ? Address.Zero
                : new Address(config.ConsumerAddress);
            var contractAddress = string.IsNullOrWhiteSpace(config.ContractAddress)
                ? Address.Zero
                : new Address(config.ContractAddress);

            var configId = config.Id;
            var configManager = services.ConfigManager;
            var logManager = services.LogManager;
            var timestamper = services.Timestamper;
            var wallet = services.Wallet;
            var readOnlyTree = new ReadOnlyBlockTree(services.BlockTree);
            var readOnlyDbProvider = new ReadOnlyDbProvider(services.RocksProvider, false);
            var readOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree,
                services.SpecProvider, logManager);
            var blockchainBridge = new BlockchainBridge(
                readOnlyTxProcessingEnv.StateReader,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                readOnlyTxProcessingEnv.BlockTree,
                services.TransactionPool,
                services.ReceiptStorage,
                services.FilterStore,
                services.FilterManager,
                wallet,
                readOnlyTxProcessingEnv.TransactionProcessor,
                services.Ecdsa,
                services.ConfigProvider.GetConfig<IRpcConfig>());
            var dataAssetRlpDecoder = new DataAssetDecoder();
            var encoder = new AbiEncoder();

            INdmBlockchainBridge ndmBlockchainBridge;
            if (config.ProxyEnabled)
            {
                services.JsonRpcClientProxy.SetUrls(config.JsonRpcUrlProxies);
                ndmBlockchainBridge = new NdmBlockchainBridgeProxy(services.EthJsonRpcClientProxy);
            }
            else
            {
                ndmBlockchainBridge = new NdmBlockchainBridge(blockchainBridge, services.TransactionPool);
            }

            var gasPriceService = new GasPriceService(services.HttpClient, configManager, configId, timestamper,
                logManager);
            var transactionService = new TransactionService(ndmBlockchainBridge, wallet, configManager, configId,
                logManager);
            var depositService = new DepositService(ndmBlockchainBridge, encoder, wallet, contractAddress);
            var ndmConsumerChannelManager = services.NdmConsumerChannelManager;
            var ndmDataPublisher = services.NdmDataPublisher;
            var jsonRpcNdmConsumerChannel = new JsonRpcNdmConsumerChannel(logManager);
            if (config.JsonRpcDataChannelEnabled)
            {
                ndmConsumerChannelManager.Add(jsonRpcNdmConsumerChannel);
            }

            return new Services(services, new NdmCreatedServices(consumerAddress, encoder, dataAssetRlpDecoder,
                depositService, gasPriceService, transactionService, ndmDataPublisher, jsonRpcNdmConsumerChannel,
                ndmConsumerChannelManager, ndmBlockchainBridge));
        }

        private static void AddDecoders()
        {
            DataDeliveryReceiptDecoder.Init();
            DataDeliveryReceiptRequestDecoder.Init();
            DataDeliveryReceiptToMergeDecoder.Init();
            DataDeliveryReceiptDetailsDecoder.Init();
            DataAssetDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetProviderDecoder.Init();
            DataRequestDecoder.Init();
            DepositDecoder.Init();
            DepositApprovalDecoder.Init();
            EarlyRefundTicketDecoder.Init();
            EthRequestDecoder.Init();
            FaucetResponseDecoder.Init();
            FaucetRequestDetailsDecoder.Init();
            SessionDecoder.Init();
            TransactionInfoDecoder.Init();
            UnitsRangeDecoder.Init();
        }

        private class Services : INdmServices
        {
            public NdmRequiredServices RequiredServices { get; }
            public NdmCreatedServices CreatedServices { get; }

            public Services(NdmRequiredServices requiredServices, NdmCreatedServices createdServices)
            {
                RequiredServices = requiredServices;
                CreatedServices = createdServices;
            }
        }
    }
}