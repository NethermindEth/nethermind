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

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;
using Nethermind.WebSockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Initializers
{
    public class NdmInitializerTests
    {
        private INdmModule _ndmModule;
        private INdmConsumersModule _ndmConsumersModule;
        private IConfigProvider _configProvider;
        private IDbProvider _dbProvider;
        private string _baseDbPath;
        private IBlockTree _blockTree;
        private ITxPool _txPool;
        private ISpecProvider _specProvider;
        private IReceiptStorage _receiptStorage;
        private IWallet _wallet;
        private IFilterStore _filterStore;
        private IFilterManager _filterManager;
        private ITimestamper _timestamper;
        private IEthereumEcdsa _ecdsa;
        private IRpcModuleProvider _rpcModuleProvider;
        private IKeyStore _keyStore;
        private IJsonSerializer _jsonSerializer;
        private ICryptoRandom _cryptoRandom;
        private IEnode _enode;
        private INdmConsumerChannelManager _consumerChannelManager;
        private INdmDataPublisher _dataPublisher;
        private IGrpcServer _grpcServer;
        private INodeStatsManager _nodeStatsManager;
        private IProtocolsManager _protocolsManager;
        private IProtocolValidator _protocolValidator;
        private IMessageSerializationService _messageSerializationService;
        private bool _enableUnsecuredDevWallet;
        private IWebSocketsManager _webSocketsManager;
        private ILogManager _logManager;
        private IBlockProcessor _blockProcessor;
        private IJsonRpcClientProxy _jsonRpcClientProxy;
        private IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private IHttpClient _httpClient;
        private IMonitoringService _monitoringService;
        private NdmConfig _ndmConfig;
        private NdmInitializer _ndmInitializer;

        [SetUp]
        public void Setup()
        {
            _ndmModule = Substitute.For<INdmModule>();
            _ndmConsumersModule = Substitute.For<INdmConsumersModule>();
            _configProvider = Substitute.For<IConfigProvider>();
            _dbProvider = Substitute.For<IDbProvider>();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _specProvider = Substitute.For<ISpecProvider>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _wallet = Substitute.For<IWallet>();
            _filterStore = Substitute.For<IFilterStore>();
            _filterManager = Substitute.For<IFilterManager>();
            _timestamper = Substitute.For<ITimestamper>();
            _ecdsa = Substitute.For<IEthereumEcdsa>();
            _rpcModuleProvider = Substitute.For<IRpcModuleProvider>();
            _keyStore = Substitute.For<IKeyStore>();
            _jsonSerializer = Substitute.For<IJsonSerializer>();
            _cryptoRandom = Substitute.For<ICryptoRandom>();
            _enode = Substitute.For<IEnode>();
            _consumerChannelManager = Substitute.For<INdmConsumerChannelManager>();
            _dataPublisher = Substitute.For<INdmDataPublisher>();
            _grpcServer = Substitute.For<IGrpcServer>();
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _protocolsManager = Substitute.For<IProtocolsManager>();
            _protocolValidator = Substitute.For<IProtocolValidator>();
            _messageSerializationService = Substitute.For<IMessageSerializationService>();
            _webSocketsManager = Substitute.For<IWebSocketsManager>();
            _logManager = LimboLogs.Instance;
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _jsonRpcClientProxy = Substitute.For<IJsonRpcClientProxy>();
            _ethJsonRpcClientProxy = Substitute.For<IEthJsonRpcClientProxy>();
            _httpClient = Substitute.For<IHttpClient>();
            _monitoringService = Substitute.For<IMonitoringService>();
            _enableUnsecuredDevWallet = false;
            _ndmConfig = new NdmConfig {Enabled = true, StoreConfigInDatabase = false};
            _configProvider.GetConfig<INdmConfig>().Returns(_ndmConfig);
            _ndmInitializer = new NdmInitializer(_ndmModule, _ndmConsumersModule);
        }

        [Test]
        public async Task database_path_should_be_base_db_and_ndm_db_path()
        {
            _baseDbPath = "db";
            _ndmConfig.DatabasePath = "ndm";
            await _ndmInitializer.InitAsync(_configProvider, _dbProvider, _baseDbPath, _blockTree,
                _txPool, _specProvider, _receiptStorage, _wallet, _filterStore, _filterManager, _timestamper, _ecdsa,
                _rpcModuleProvider, _keyStore, _jsonSerializer, _cryptoRandom, _enode, _consumerChannelManager,
                _dataPublisher, _grpcServer, _nodeStatsManager, _protocolsManager, _protocolValidator,
                _messageSerializationService, _enableUnsecuredDevWallet, _webSocketsManager, _logManager,
                _blockProcessor, _jsonRpcClientProxy, _ethJsonRpcClientProxy, _httpClient, _monitoringService);
            _ndmInitializer.DbPath.Should().Be(Path.Combine(_baseDbPath, _ndmConfig.DatabasePath));
        }
    }
}