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
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Discovery;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Serializers;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
using Nethermind.Mining;
using Nethermind.Network;
using Nethermind.Runner.Runners;
using Nethermind.Store;
using ILogger = Nethermind.Core.ILogger;

namespace Nethermind.Runner
{
    // TODO use here only what is needed in JSON RPC, pass implementations, do not use Bootstrap class outside of JSON RPC
    // I guess we will only need BlockTree, signer, and some of the state / stores classes
    public static class Bootstrap
    {
        public static IServiceCollection ServiceCollection { get; private set; }
        public static IServiceProvider ServiceProvider { get; set; }

        public static void ConfigureContainer(JsonRpc.IConfigurationProvider configurationProvider, IDiscoveryConfigurationProvider discoveryConfigurationProvider, IPrivateKeyProvider privateKeyProvider, ILogger logger, InitParams initParams)
        {
            var services = new ServiceCollection();

            services.AddSingleton(configurationProvider);
            services.AddSingleton(logger);
            services.AddSingleton(privateKeyProvider);

            //based on configuration we will set it
            //var specProvider = new MainNetSpecProvider();
            var homesteadBlockNr = initParams.HomesteadBlockNr;

            var specProvider = homesteadBlockNr.HasValue
                ? (ISpecProvider)new CustomSpecProvider((0, Frontier.Instance), (homesteadBlockNr.Value, Homestead.Instance))
                : new MainNetSpecProvider();

            var ethereumRelease = specProvider.GetSpec(1);
            var chainId = ChainId.MainNet;

            var signer = new EthereumSigner(specProvider, logger);
            var signatureValidator = new SignatureValidator(chainId); // TODO: review, check with spec provider

            services.AddSingleton(specProvider);
            var blockTree = new BlockTree(new MemDb(), new MemDb(), specProvider, logger); // TODO: temp, change

            services.AddSingleton<IBlockTree>(blockTree);
            services.AddSingleton(ethereumRelease);
            services.AddSingleton<IEthereumSigner>(signer);
            services.AddSingleton<ISigner>(signer);
            services.AddSingleton<ISignatureValidator>(signatureValidator);

            services.AddSingleton<IEthash, Ethash>();
            services.AddSingleton<ISealEngine, EthashSealEngine>();
            services.AddSingleton<IHeaderValidator, HeaderValidator>();
            services.AddSingleton<IOmmersValidator, OmmersValidator>();
            services.AddSingleton<ITransactionValidator, TransactionValidator>();
            services.AddSingleton<IBlockValidator, BlockValidator>();

            services.AddSingleton<IDb, MemDb>(); // TODO: temp change
            services.AddSingleton<StateTree>();
            services.AddSingleton<IStateProvider, StateProvider>();
            services.AddSingleton<IDbProvider, MemDbProvider>();
            services.AddSingleton<IStorageProvider, StorageProvider>();

            services.AddSingleton<IBlockhashProvider, BlockhashProvider>();
            services.AddSingleton<IVirtualMachine, VirtualMachine>();
            services.AddSingleton<ITransactionProcessor, TransactionProcessor>();
            services.AddSingleton<ITransactionStore, TransactionStore>();

            services.AddSingleton<IDifficultyCalculator, DifficultyCalculator>();
            services.AddSingleton<IRewardCalculator, RewardCalculator>();
            services.AddSingleton<IBlockProcessor, BlockProcessor>();
            services.AddSingleton<IBlockchainProcessor, BlockchainProcessor>();

            services.AddSingleton<KeyStore.IConfigurationProvider, KeyStore.ConfigurationProvider>();
            services.AddSingleton<IJsonSerializer, JsonSerializer>();
            services.AddSingleton<ISymmetricEncrypter, AesEncrypter>();
            services.AddSingleton<ICryptoRandom, CryptoRandom>();
            services.AddSingleton<IKeyStore, FileKeyStore>();
            
            //JsonRPC
            services.AddSingleton<IJsonRpcModelMapper, JsonRpcModelMapper>();
            services.AddSingleton<IModuleProvider, ModuleProvider>();
            services.AddSingleton<INetModule, NetModule>();
            services.AddSingleton<IWeb3Module, Web3Module>();
            services.AddSingleton<IEthModule, EthModule>();
            services.AddSingleton<IShhModule, ShhModule>();
            services.AddSingleton<IJsonRpcService, JsonRpcService>();

            //Discovery
            services.AddSingleton<INetworkHelper, NetworkHelper>();
            services.AddSingleton<IDiscoveryConfigurationProvider>(discoveryConfigurationProvider);
            services.AddTransient<INodeFactory, NodeFactory>();
            services.AddSingleton<INodeDistanceCalculator, NodeDistanceCalculator>();
            services.AddSingleton<INodeTable, NodeTable>();
            services.AddSingleton<IEvictionManager, EvictionManager>();
            services.AddTransient<IDiscoveryMessageFactory, DiscoveryMessageFactory>();
            services.AddSingleton<INodeLifecycleManagerFactory, NodeLifecycleManagerFactory>();
            services.AddSingleton<IDiscoveryManager, DiscoveryManager>();
            services.AddSingleton<INodesLocator, NodesLocator>();
            services.AddTransient<IDiscoveryMessageFactory, DiscoveryMessageFactory>();
            services.AddSingleton<INodeIdResolver, NodeIdResolver>();
            services.AddSingleton<IMessageSerializationService, MessageSerializationService>();
            services.AddSingleton<IDiscoveryMsgSerializersProvider, DiscoveryMsgSerializersProvider>();
            services.AddSingleton<IDiscoveryApp, DiscoveryApp>();

            services.AddSingleton<IDiscoveryRunner, DiscoveryRunner>();
            if (initParams.EthereumRunnerType == EthereumRunnerType.Hive)
            {
                services.AddSingleton<IEthereumRunner, HiveEthereumRunner>();
            }
            else
            {
                services.AddSingleton<IEthereumRunner, EthereumRunner>();
            }

            ServiceCollection = services;
        }
    }
}