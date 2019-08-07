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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public class NdmRequiredServices
    {
        public IConfigProvider ConfigProvider { get; }
        public IConfigManager ConfigManager { get; }
        public INdmConfig NdmConfig { get; }
        public string BaseDbPath { get; }
        public IDbProvider RocksProvider { get; }
        public IMongoProvider MongoProvider { get; }
        public ILogManager LogManager { get; }
        public IBlockTree BlockTree { get; }
        public ITxPool TransactionPool { get; }
        public ISpecProvider SpecProvider { get; }
        public IReceiptStorage ReceiptStorage { get; }
        public IFilterStore FilterStore { get; }
        public IFilterManager FilterManager { get; }
        public IWallet Wallet { get; }
        public ITimestamper Timestamper { get; }
        public IEthereumEcdsa Ecdsa { get; }
        public IKeyStore KeyStore { get; }
        public IRpcModuleProvider RpcModuleProvider { get; }
        public IJsonSerializer JsonSerializer { get; }
        public ICryptoRandom CryptoRandom { get; }
        public IEnode Enode { get; }
        public INdmConsumerChannelManager NdmConsumerChannelManager { get; }
        public INdmDataPublisher NdmDataPublisher { get; }
        public IGrpcServer GrpcServer { get; }
        public IEthRequestService EthRequestService { get; }
        public INdmNotifier Notifier { get; }
        public bool EnableUnsecuredDevWallet { get; }
        public IBlockProcessor BlockProcessor { get; }

        public NdmRequiredServices(IConfigProvider configProvider, IConfigManager configManager, INdmConfig ndmConfig,
            string baseDbPath, IDbProvider rocksProvider, IMongoProvider mongoProvider, ILogManager logManager,
            IBlockTree blockTree, ITxPool transactionPool, ISpecProvider specProvider, IReceiptStorage receiptStorage,
            IFilterStore filterStore, IFilterManager filterManager, IWallet wallet, ITimestamper timestamper,
            IEthereumEcdsa ecdsa, IKeyStore keyStore, IRpcModuleProvider rpcModuleProvider,
            IJsonSerializer jsonSerializer, ICryptoRandom cryptoRandom, IEnode enode,
            INdmConsumerChannelManager ndmConsumerChannelManager, INdmDataPublisher ndmDataPublisher,
            IGrpcServer grpcServer, IEthRequestService ethRequestService, INdmNotifier notifier,
            bool enableUnsecuredDevWallet, IBlockProcessor blockProcessor)
        {
            ConfigProvider = configProvider;
            ConfigManager = configManager;
            NdmConfig = ndmConfig;
            BaseDbPath = baseDbPath;
            RocksProvider = rocksProvider;
            MongoProvider = mongoProvider;
            LogManager = logManager;
            BlockTree = blockTree;
            TransactionPool = transactionPool;
            SpecProvider = specProvider;
            ReceiptStorage = receiptStorage;
            FilterStore = filterStore;
            FilterManager = filterManager;
            Wallet = wallet;
            Timestamper = timestamper;
            Ecdsa = ecdsa;
            KeyStore = keyStore;
            RpcModuleProvider = rpcModuleProvider;
            JsonSerializer = jsonSerializer;
            CryptoRandom = cryptoRandom;
            Enode = enode;
            NdmConsumerChannelManager = ndmConsumerChannelManager;
            NdmDataPublisher = ndmDataPublisher;
            GrpcServer = grpcServer;
            EthRequestService = ethRequestService;
            Notifier = notifier;
            EnableUnsecuredDevWallet = enableUnsecuredDevWallet;
            BlockProcessor = blockProcessor;
        }
    }
}