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

using System.Security;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db.Config;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public static class NdmModule
    {
        public static IServices Init(RequiredServices services)
        {
            AddDecoders();
            var config = services.NdmConfig;
            var providerAddress = string.IsNullOrWhiteSpace(config.ProviderAddress)
                ? Address.Zero
                : new Address(config.ProviderAddress);
            var consumerAddress = string.IsNullOrWhiteSpace(config.ConsumerAddress)
                ? Address.Zero
                : new Address(config.ConsumerAddress);
            UnlockHardcodedAccounts(providerAddress, consumerAddress, services.Wallet);
            var readOnlyDbProvider = new ReadOnlyDbProvider(services.RocksProvider, false);
            var filterStore = new FilterStore();
            var filterManager = new FilterManager(filterStore, services.BlockProcessor, services.TransactionPool,
                services.LogManager);
            var state = new RpcState(services.BlockTree, services.SpecProvider, readOnlyDbProvider,
                services.LogManager);
            var blockchainBridge = new BlockchainBridge(
                state.StateReader,
                state.StateProvider,
                state.StorageProvider,
                state.BlockTree,
                services.TransactionPool,
                services.TransactionPoolInfoProvider,
                services.ReceiptStorage,
                filterStore,
                filterManager,
                services.Wallet,
                state.TransactionProcessor);
            var dataHeaderRlpDecoder = new DataHeaderDecoder();
            var encoder = new AbiEncoder();
            var depositService = new DepositService(blockchainBridge, encoder, services.Wallet, config,
                LimboLogs.Instance);
            var ndmConsumerChannelManager = services.NdmConsumerChannelManager;
            var ndmDataPublisher = services.NdmDataPublisher;
            var jsonRpcNdmConsumerChannel = new JsonRpcNdmConsumerChannel();
//            ndmConsumerChannelManager.Add(jsonRpcNdmConsumerChannel);

            return new Services(services, new CreatedServices(consumerAddress, encoder, dataHeaderRlpDecoder,
                depositService, ndmDataPublisher, jsonRpcNdmConsumerChannel, ndmConsumerChannelManager,
                blockchainBridge));
        }

        private static void AddDecoders()
        {
            DataDeliveryReceiptDecoder.Init();
            DataDeliveryReceiptRequestDecoder.Init();
            DataDeliveryReceiptToMergeDecoder.Init();
            DataDeliveryReceiptDetailsDecoder.Init();
            DataHeaderDecoder.Init();
            DataHeaderRuleDecoder.Init();
            DataHeaderRulesDecoder.Init();
            DataHeaderProviderDecoder.Init();
            DataRequestDecoder.Init();
            DepositDecoder.Init();
            DepositApprovalDecoder.Init();
            EarlyRefundTicketDecoder.Init();
            EthRequestDecoder.Init();
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

        private class RpcState
        {
            public readonly IStateReader StateReader;
            public readonly IStateProvider StateProvider;
            public readonly IStorageProvider StorageProvider;
            public readonly IBlockhashProvider BlockhashProvider;
            public readonly IVirtualMachine VirtualMachine;
            public readonly TransactionProcessor TransactionProcessor;
            public readonly IBlockTree BlockTree;

            public RpcState(IBlockTree blockTree, ISpecProvider specProvider, IReadOnlyDbProvider readOnlyDbProvider,
                ILogManager logManager)
            {
                var stateDb = readOnlyDbProvider.StateDb;
                var codeDb = readOnlyDbProvider.CodeDb;
                StateReader = new StateReader(readOnlyDbProvider.StateDb, codeDb, logManager);
                StateProvider = new StateProvider(stateDb, codeDb, logManager);
                StorageProvider = new StorageProvider(stateDb, StateProvider, logManager);
                BlockTree = new ReadOnlyBlockTree(blockTree);
                BlockhashProvider = new BlockhashProvider(BlockTree, logManager);
                VirtualMachine = new VirtualMachine(StateProvider, StorageProvider, BlockhashProvider, logManager);
                TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, StorageProvider,
                    VirtualMachine, logManager);
            }
        }

        public class RequiredServices
        {
            public IConfigProvider ConfigProvider { get; }
            public IConfigManager ConfigManager { get; }
            public INdmConfig NdmConfig { get; }
            public string BaseDbPath { get; }
            public IDbProvider RocksProvider { get; }
            public IMongoProvider MongoProvider { get; }
            public ILogManager LogManager { get; }
            public IBlockProcessor BlockProcessor { get; }
            public IBlockTree BlockTree { get; }
            public ITxPool TransactionPool { get; }
            public ITxPoolInfoProvider TransactionPoolInfoProvider { get; }
            public ISpecProvider SpecProvider { get; }
            public IReceiptStorage ReceiptStorage { get; }
            public IWallet Wallet { get; }
            public ITimestamp Timestamp { get; }
            public IEcdsa Ecdsa { get; }
            public IKeyStore KeyStore { get; }
            public IRpcModuleProvider RpcModuleProvider { get; }
            public IJsonSerializer JsonSerializer { get; }
            public ICryptoRandom CryptoRandom { get; }
            public IEnode Enode { get; }
            public INdmConsumerChannelManager NdmConsumerChannelManager { get; }
            public INdmDataPublisher NdmDataPublisher { get; }
            public IGrpcService GrpcService { get; }
            public EthRequestService EthRequestService { get; }
            public bool EnableUnsecuredDevWallet { get; }

            public RequiredServices(IConfigProvider configProvider, IConfigManager configManager, INdmConfig ndmConfig,
                string baseDbPath, IDbProvider rocksProvider, IMongoProvider mongoProvider, ILogManager logManager,
                IBlockProcessor blockProcessor, IBlockTree blockTree, ITxPool transactionPool,
                ITxPoolInfoProvider transactionPoolInfoProvider, ISpecProvider specProvider, 
                IReceiptStorage receiptStorage, IWallet wallet, ITimestamp timestamp, IEcdsa ecdsa,
                IKeyStore keyStore, IRpcModuleProvider rpcModuleProvider, IJsonSerializer jsonSerializer,
                ICryptoRandom cryptoRandom, IEnode enode, INdmConsumerChannelManager ndmConsumerChannelManager,
                INdmDataPublisher ndmDataPublisher, IGrpcService grpcService, EthRequestService ethRequestService,
                bool enableUnsecuredDevWallet)
            {
                ConfigProvider = configProvider;
                ConfigManager = configManager;
                NdmConfig = ndmConfig;
                BaseDbPath = baseDbPath;
                RocksProvider = rocksProvider;
                MongoProvider = mongoProvider;
                LogManager = logManager;
                BlockProcessor = blockProcessor;
                BlockTree = blockTree;
                TransactionPool = transactionPool;
                TransactionPoolInfoProvider = transactionPoolInfoProvider;
                SpecProvider = specProvider;
                ReceiptStorage = receiptStorage;
                Wallet = wallet;
                Timestamp = timestamp;
                Ecdsa = ecdsa;
                KeyStore = keyStore;
                RpcModuleProvider = rpcModuleProvider;
                JsonSerializer = jsonSerializer;
                CryptoRandom = cryptoRandom;
                Enode = enode;
                NdmConsumerChannelManager = ndmConsumerChannelManager;
                NdmDataPublisher = ndmDataPublisher;
                GrpcService = grpcService;
                EthRequestService = ethRequestService;
                EnableUnsecuredDevWallet = enableUnsecuredDevWallet;
            }
        }

        public class CreatedServices
        {
            public Address ConsumerAddress { get; }
            public IAbiEncoder AbiEncoder { get; }
            public IRlpDecoder<DataHeader> DataHeaderRlpDecoder { get; }
            public IDepositService DepositService { get; }
            public INdmDataPublisher NdmDataPublisher { get; }
            public IJsonRpcNdmConsumerChannel JsonRpcNdmConsumerChannel { get; }
            public INdmConsumerChannelManager NdmConsumerChannelManager { get; }
            public IBlockchainBridge BlockchainBridge { get; }

            public CreatedServices(Address consumerAddress,
                IAbiEncoder abiEncoder, IRlpDecoder<DataHeader> dataHeaderRlpDecoder, IDepositService depositService,
                INdmDataPublisher ndmDataPublisher, IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel,
                INdmConsumerChannelManager ndmConsumerChannelManager, IBlockchainBridge blockchainBridge)
            {
                ConsumerAddress = consumerAddress;
                AbiEncoder = abiEncoder;
                DataHeaderRlpDecoder = dataHeaderRlpDecoder;
                DepositService = depositService;
                NdmDataPublisher = ndmDataPublisher;
                JsonRpcNdmConsumerChannel = jsonRpcNdmConsumerChannel;
                NdmConsumerChannelManager = ndmConsumerChannelManager;
                BlockchainBridge = blockchainBridge;
            }
        }

        public interface IServices
        {
            RequiredServices RequiredServices { get; }
            CreatedServices CreatedServices { get; }
        }

        private class Services : IServices
        {
            public RequiredServices RequiredServices { get; }
            public CreatedServices CreatedServices { get; }

            public Services(RequiredServices requiredServices, CreatedServices createdServices)
            {
                RequiredServices = requiredServices;
                CreatedServices = createdServices;
            }
        }
    }
}