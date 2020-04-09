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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
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
        public IReceiptFinder ReceiptFinder { get; }
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
        public IJsonRpcClientProxy? JsonRpcClientProxy { get; }
        public IEthJsonRpcClientProxy? EthJsonRpcClientProxy { get; }
        public IHttpClient HttpClient { get; }
        public IMonitoringService MonitoringService { get; }
        public IBloomStorage BloomStorage { get; }

        public NdmRequiredServices(
            IConfigProvider configProvider,
            IConfigManager configManager,
            INdmConfig ndmConfig,
            string baseDbPath,
            IDbProvider rocksProvider,
            IMongoProvider mongoProvider,
            ILogManager logManager,
            IBlockTree blockTree,
            ITxPool transactionPool,
            ISpecProvider specProvider,
            IReceiptFinder receiptFinder,
            IFilterStore filterStore,
            IFilterManager filterManager,
            IWallet wallet,
            ITimestamper timestamper,
            IEthereumEcdsa ecdsa,
            IKeyStore keyStore,
            IRpcModuleProvider rpcModuleProvider,
            IJsonSerializer jsonSerializer,
            ICryptoRandom cryptoRandom,
            IEnode enode,
            INdmConsumerChannelManager ndmConsumerChannelManager,
            INdmDataPublisher ndmDataPublisher,
            IGrpcServer grpcServer,
            IEthRequestService ethRequestService,
            INdmNotifier notifier,
            bool enableUnsecuredDevWallet,
            IBlockProcessor blockProcessor,
            IJsonRpcClientProxy? jsonRpcClientProxy,
            IEthJsonRpcClientProxy? ethJsonRpcClientProxy,
            IHttpClient httpClient,
            IMonitoringService monitoringService,
            IBloomStorage bloomStorage)
        {
            ConfigProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            ConfigManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            NdmConfig = ndmConfig ?? throw new ArgumentNullException(nameof(ndmConfig));
            BaseDbPath = baseDbPath ?? throw new ArgumentNullException(nameof(baseDbPath));
            RocksProvider = rocksProvider ?? throw new ArgumentNullException(nameof(rocksProvider));
            MongoProvider = mongoProvider ?? throw new ArgumentNullException(nameof(mongoProvider));
            LogManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            TransactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            SpecProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            ReceiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            FilterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            FilterManager = filterManager ?? throw new ArgumentNullException(nameof(filterManager));
            Wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            Timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            Ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            KeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            RpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
            JsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            CryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            Enode = enode ?? throw new ArgumentNullException(nameof(enode));
            NdmConsumerChannelManager = ndmConsumerChannelManager ?? throw new ArgumentNullException(nameof(ndmConsumerChannelManager));
            NdmDataPublisher = ndmDataPublisher ?? throw new ArgumentNullException(nameof(ndmDataPublisher));
            GrpcServer = grpcServer ?? throw new ArgumentNullException(nameof(grpcServer));
            EthRequestService = ethRequestService ?? throw new ArgumentNullException(nameof(ethRequestService));
            Notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            EnableUnsecuredDevWallet = enableUnsecuredDevWallet;
            BlockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            MonitoringService = monitoringService ?? throw new ArgumentNullException(nameof(monitoringService));
            JsonRpcClientProxy = jsonRpcClientProxy;
            EthJsonRpcClientProxy = ethJsonRpcClientProxy;
            BloomStorage = bloomStorage;
        }
    }
}