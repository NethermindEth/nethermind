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

using System.Linq;
using System.Net.Http;
using System.Security;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Infrastructure.Modules
{
    public class NdmModule : INdmModule
    {
        public INdmServices Init(NdmRequiredServices services)
        {
            AddDecoders();
            var config = services.NdmConfig;
            var providerAddress = string.IsNullOrWhiteSpace(config.ProviderAddress)
                ? Address.Zero
                : new Address(config.ProviderAddress);
            var consumerAddress = string.IsNullOrWhiteSpace(config.ConsumerAddress)
                ? Address.Zero
                : new Address(config.ConsumerAddress);
            var contractAddress = string.IsNullOrWhiteSpace(config.ContractAddress)
                ? Address.Zero
                : new Address(config.ContractAddress);
            UnlockHardcodedAccounts(providerAddress, consumerAddress, services.Wallet);

            var logManager = services.LogManager;
            var jsonSerializer = services.JsonSerializer;
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
                services.Wallet,
                readOnlyTxProcessingEnv.TransactionProcessor,
                services.Ecdsa);
            var dataAssetRlpDecoder = new DataAssetDecoder();
            var encoder = new AbiEncoder();

            IEthJsonRpcClientProxy ethJsonRpcClientProxy = null;
            INdmBlockchainBridge ndmBlockchainBridge;
            if (config.ProxyEnabled)
            {
                ethJsonRpcClientProxy = new EthJsonRpcClientProxy(new JsonRpcClientProxy(
                    new DefaultHttpClient(new HttpClient(), jsonSerializer, logManager),
                    config.JsonRpcUrlProxies));
                ndmBlockchainBridge = new NdmBlockchainBridgeProxy(ethJsonRpcClientProxy);
            }
            else
            {
                ndmBlockchainBridge = new NdmBlockchainBridge(blockchainBridge, services.TransactionPool);
            }
            
            var depositService = new DepositService(ndmBlockchainBridge, services.TransactionPool, encoder,
                services.Wallet, contractAddress, logManager);
            var ndmConsumerChannelManager = services.NdmConsumerChannelManager;
            var ndmDataPublisher = services.NdmDataPublisher;
            var jsonRpcNdmConsumerChannel = new JsonRpcNdmConsumerChannel();
//            ndmConsumerChannelManager.Add(jsonRpcNdmConsumerChannel);

            return new Services(services, new NdmCreatedServices(consumerAddress, encoder, dataAssetRlpDecoder,
                depositService, ndmDataPublisher, jsonRpcNdmConsumerChannel, ndmConsumerChannelManager,
                ndmBlockchainBridge, ethJsonRpcClientProxy));
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
            UnitsRangeDecoder.Init();
        }

        private static void UnlockHardcodedAccounts(Address providerAddress, Address consumerAddress, IWallet wallet)
        {
            // hardcoded passwords
            var consumerPassphrase = new SecureString();
            foreach (var c in "ndmConsumer")
            {
                consumerPassphrase.AppendChar(c);
            }

            consumerPassphrase.MakeReadOnly();
            wallet.UnlockAccount(consumerAddress, consumerPassphrase);
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