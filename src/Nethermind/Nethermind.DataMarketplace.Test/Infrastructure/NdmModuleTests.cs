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

using FluentAssertions;
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
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Store;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Infrastructure
{
    public class NdmModuleTests
    {
        private IConfigProvider _configProvider;
        private IConfigManager _configManager;
        private INdmConfig _ndmConfig;
        private string _baseDbPath;
        private IDbProvider _rocksProvider;
        private IMongoProvider _mongoProvider;
        private ILogManager _logManager;
        private IBlockTree _blockTree;
        private ISpecProvider _specProvider;
        private ITxPool _transactionPool;
        private IReceiptStorage _receiptStorage;
        private IFilterStore _filterStore;
        private IFilterManager _filterManager;
        private IWallet _wallet;
        private ITimestamper _timestamper;
        private IEthereumEcdsa _ecdsa;
        private IKeyStore _keyStore;
        private IRpcModuleProvider _rpcModuleProvider;
        private IJsonSerializer _jsonSerializer;
        private ICryptoRandom _cryptoRandom;
        private IEnode _enode;
        private INdmConsumerChannelManager _ndmConsumerChannelManager;
        private INdmDataPublisher _ndmDataPublisher;
        private IGrpcServer _grpcServer;
        private IEthRequestService _ethRequestService;
        private INdmNotifier _notifier;
        private bool _enableUnsecuredDevWallet;
        private IBlockProcessor _blockProcessor;
        private INdmModule _ndmModule;

        [SetUp]
        public void Setup()
        {
            _configProvider = Substitute.For<IConfigProvider>();
            _configManager = Substitute.For<IConfigManager>();
            _ndmConfig = new NdmConfig();
            _baseDbPath = "db";
            _rocksProvider = Substitute.For<IDbProvider>();
            _mongoProvider = Substitute.For<IMongoProvider>();
            _logManager = Substitute.For<ILogManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _specProvider = Substitute.For<ISpecProvider>();
            _transactionPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _filterStore = Substitute.For<IFilterStore>();
            _filterManager = Substitute.For<IFilterManager>();
            _wallet = Substitute.For<IWallet>();
            _timestamper = Substitute.For<ITimestamper>();
            _ecdsa = Substitute.For<IEthereumEcdsa>();
            _keyStore = Substitute.For<IKeyStore>();
            _rpcModuleProvider = Substitute.For<IRpcModuleProvider>();
            _jsonSerializer = Substitute.For<IJsonSerializer>();
            _cryptoRandom = Substitute.For<ICryptoRandom>();
            _enode = Substitute.For<IEnode>();
            _ndmConsumerChannelManager = Substitute.For<INdmConsumerChannelManager>();
            _ndmDataPublisher = Substitute.For<INdmDataPublisher>();
            _grpcServer = Substitute.For<IGrpcServer>();
            _ethRequestService = Substitute.For<IEthRequestService>();
            _notifier = Substitute.For<INdmNotifier>();
            _enableUnsecuredDevWallet = false;
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _ndmModule = new NdmModule();
        }

        [Test]
        public void init_should_return_services()
        {
            var services = _ndmModule.Init(new NdmRequiredServices(_configProvider, _configManager, _ndmConfig,
                _baseDbPath, _rocksProvider, _mongoProvider, _logManager, _blockTree, _transactionPool, _specProvider,
                _receiptStorage, _filterStore, _filterManager, _wallet, _timestamper, _ecdsa, _keyStore,
                _rpcModuleProvider, _jsonSerializer, _cryptoRandom, _enode, _ndmConsumerChannelManager,
                _ndmDataPublisher, _grpcServer, _ethRequestService, _notifier, _enableUnsecuredDevWallet,
                _blockProcessor));
            services.Should().NotBeNull();
            services.CreatedServices.Should().NotBeNull();
            services.RequiredServices.Should().NotBeNull();
        }
    }
}