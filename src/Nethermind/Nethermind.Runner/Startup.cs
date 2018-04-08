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
using System.ComponentModel;
using LightInject;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
using Nethermind.Mining;
using Nethermind.Store;

namespace Nethermind.Runner
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            //var provider = new LightInjectServiceProvider(Bootstraper.Container);
            //provider.Build(services);
            RegisterApplicationTypes(services);
        }

        private void RegisterApplicationTypes(IServiceCollection services)
        {
            //based on configuration we will set it
            //var specProvider = new MainNetSpecProvider();
            var homesteadBlockNr = RunnerApp.InitParams.HomesteadBlockNr;
            
            var specProvider = homesteadBlockNr.HasValue 
                ? (ISpecProvider)new CustomSpecProvider((0, Frontier.Instance), (homesteadBlockNr.Value, Homestead.Instance)) 
                : new MainNetSpecProvider();

            var ethereumRelease = specProvider.GetSpec(1);
            var chainId = ChainId.MainNet;

            var dynamicReleaseSpec = new DynamicReleaseSpec(specProvider);
            var signer = new EthereumSigner(specProvider, NullLogger.Instance);
            var signatureValidator = new SignatureValidator(dynamicReleaseSpec, chainId); // TODO: review, check with spec provider

            services.AddSingleton<ISpecProvider>(specProvider);
            services.AddTransient<ILogger, ConsoleLogger>();
            services.AddSingleton<IBlockStore, BlockStore>();
            services.AddSingleton(ethereumRelease);
            services.AddSingleton<IEthereumSigner>(signer);
            services.AddSingleton<ISignatureValidator>(signatureValidator);

            services.AddSingleton<IEthash, Ethash>();
            services.AddSingleton<IHeaderValidator, HeaderValidator>();
            services.AddSingleton<IOmmersValidator, OmmersValidator>();
            services.AddSingleton<ITransactionValidator, TransactionValidator>();
            services.AddSingleton<IBlockValidator, BlockValidator>();

            services.AddSingleton<IDb, InMemoryDb>();
            services.AddSingleton<StateTree>();
            services.AddSingleton<IStateProvider, StateProvider>();
            services.AddSingleton<IDbProvider, DbProvider>();
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

            services.AddSingleton<JsonRpc.IConfigurationProvider, JsonRpc.ConfigurationProvider>();
            services.AddSingleton<IJsonRpcModelMapper, JsonRpcModelMapper>();
            services.AddSingleton<IModuleProvider, ModuleProvider>();
            services.AddSingleton<INetModule, NetModule>();
            services.AddSingleton<IWeb3Module, Web3Module>();
            services.AddSingleton<IEthModule, EthModule>();
            services.AddSingleton<IShhModule, ShhModule>();
            services.AddSingleton<IJsonRpcService, JsonRpcService>();
            services.AddSingleton<IJsonRpcRunner, JsonRpcRunner>();
            services.AddSingleton<IRunner, EthereumRunner>();

            //var logger = new ConsoleLogger();

            //var blockStore = new BlockStore();
            //IEthereumRelease release = Frontier.Instance;
            //var blockValidator = new BlockValidator(new TransactionValidator(release, new SignatureValidator(release, ChainId.DefaultGethPrivateChain)), new HeaderValidator(blockStore), new OmmersValidator(blockStore, new HeaderValidator(blockStore)), logger);
            //var db = new InMemoryDb();
            //var stateProvider = new StateProvider(new StateTree(db), release, logger);
            //var storageProvider = new StorageProvider(new MultiDb(logger), stateProvider, logger);
            //var virtualMachine = new VirtualMachine(release, stateProvider, storageProvider, new BlockhashProvider(blockStore), logger);
            ////var signer = new EthereumSigner(release, ChainId.MainNet);
            //var transactionProcessor = new TransactionProcessor(release, stateProvider, storageProvider, virtualMachine, signer, logger);

            //var transactionStore = new TransactionStore();
            //var blockProcessor = new BlockProcessor(release, blockStore, blockValidator, new ProtocolBasedDifficultyCalculator(release), new RewardCalculator(release), transactionProcessor,
            //    storageProvider, stateProvider, storageProvider, transactionStore, logger);

            //var keyStoreConfigurationProvider = new KeyStore.ConfigurationProvider();
            //var blockchaninProcessor = new BlockchainProcessor(blockProcessor, blockStore, logger);
            //var keyStore = new FileKeyStore(keyStoreConfigurationProvider, new JsonSerializer(logger), new AesEncrypter(keyStoreConfigurationProvider, logger), new CryptoRandom(), logger);

            //_ethModule = new EthModule(logger, new JsonSerializer(logger), blockchaninProcessor, stateProvider, keyStore, new ConfigurationProvider(), blockStore, db, new JsonRpcModelMapper(signer), release, transactionStore);
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseMvc();
        }
    }
}
