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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Core.Utils;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Config;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Config;
using Nethermind.Runner.Runners;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.Runner
{
    public abstract class RunnerAppBase
    {
        protected ILogger Logger;
        private IRunner _jsonRpcRunner = NullRunner.Instance;
        private IRunner _ethereumRunner = NullRunner.Instance;
        private TaskCompletionSource<object> _cancelKeySource;

        protected RunnerAppBase(ILogger logger)
        {
            Logger = logger;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
        }

        private void CurrentDomainOnProcessExit(object sender, EventArgs e)
        {
        }

        private void LogMemoryConfiguration()
        {
            if (Logger.IsDebug) Logger.Debug($"Server GC           : {System.Runtime.GCSettings.IsServerGC}");
            if (Logger.IsDebug) Logger.Debug($"GC latency mode     : {System.Runtime.GCSettings.LatencyMode}");
            if (Logger.IsDebug)
                Logger.Debug($"LOH compaction mode : {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");
        }

        public void Run(string[] args)
        {
            var (app, buildConfigProvider, getDbBasePath) = BuildCommandLineApp();
            ManualResetEvent appClosed = new ManualResetEvent(false);
            app.OnExecute(async () =>
            {
                var configProvider = buildConfigProvider();
                var initConfig = configProvider.GetConfig<IInitConfig>();
                if (initConfig.RemovingLogFilesEnabled)
                {
                    RemoveLogFiles(initConfig.LogDirectory);
                }

                Logger = new NLogLogger(initConfig.LogFileName, initConfig.LogDirectory);
                LogMemoryConfiguration();

                var pathDbPath = getDbBasePath();
                if (!string.IsNullOrWhiteSpace(pathDbPath))
                {
                    var newDbPath = Path.Combine(pathDbPath, initConfig.BaseDbPath);
                    if (Logger.IsInfo)
                        Logger.Info(
                            $"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");
                    initConfig.BaseDbPath = newDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");
                }

                Console.Title = initConfig.LogFileName;
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;

                var serializer = new UnforgivingJsonSerializer();
                if (Logger.IsInfo)
                    Logger.Info($"Running Nethermind Runner, parameters:\n{serializer.Serialize(initConfig, true)}\n");

                _cancelKeySource = new TaskCompletionSource<object>();
                Task userCancelTask = Task.Factory.StartNew(() =>
                {
                    Console.WriteLine("Enter 'e' to exit");
                    while (true)
                    {
                        var line = Console.ReadLine();
                        if (line == "e")
                        {
                            break;
                        }
                    }
                });

                await StartRunners(configProvider);
                await Task.WhenAny(userCancelTask, _cancelKeySource.Task);

                Console.WriteLine("Closing, please wait until all functions are stopped properly...");
                StopAsync().Wait();
                Console.WriteLine("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            app.Execute(args);
            appClosed.WaitOne();
        }

        private void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            _cancelKeySource.SetResult(null);
            e.Cancel = false;
        }

        [Todo(Improve.Refactor, "network config can be handled internally in EthereumRunner")]
        protected async Task StartRunners(IConfigProvider configProvider)
        {
            var initParams = configProvider.GetConfig<IInitConfig>();
            var logManager = new NLogManager(initParams.LogFileName, initParams.LogDirectory);
            IRpcModuleProvider rpcModuleProvider = initParams.JsonRpcEnabled
                ? new RpcModuleProvider(configProvider.GetConfig<IJsonRpcConfig>())
                : (IRpcModuleProvider) NullModuleProvider.Instance;

            if (Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true")
            {
                _ethereumRunner = GetHiveRunner(rpcModuleProvider, configProvider, logManager);
            }
            else if (initParams.RunAsReceiptsFiller)
            {
                _ethereumRunner = new ReceiptsFiller(configProvider, logManager);
            }
            else
            {
                _ethereumRunner = new EthereumRunner(rpcModuleProvider, configProvider, logManager);
            }

            await _ethereumRunner.Start().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError) Logger.Error("Error during ethereum runner start", x.Exception);
            });

            if (initParams.JsonRpcEnabled && !initParams.RunAsReceiptsFiller)
            {
                var serializer = new UnforgivingJsonSerializer();
                rpcModuleProvider.Register<INethmModule>(new NethmModule(configProvider, logManager, serializer));
                rpcModuleProvider.Register<IShhModule>(new ShhModule(configProvider, logManager, serializer));
                rpcModuleProvider.Register<IWeb3Module>(new Web3Module(configProvider, logManager, serializer));

                Bootstrap.Instance.JsonRpcService = new JsonRpcService(rpcModuleProvider, configProvider, logManager);
                Bootstrap.Instance.LogManager = logManager;
                Bootstrap.Instance.JsonSerializer = serializer;
                _jsonRpcRunner = new JsonRpcRunner(configProvider, rpcModuleProvider, logManager);
                await _jsonRpcRunner.Start().ContinueWith(x =>
                {
                    if (x.IsFaulted && Logger.IsError) Logger.Error("Error during jsonRpc runner start", x.Exception);
                });
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("Json RPC is disabled");
            }
        }

        protected abstract (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp();

        protected async Task StopAsync()
        {
            _jsonRpcRunner?.StopAsync(); // do not await
            var ethereumTask = _ethereumRunner?.StopAsync() ?? Task.CompletedTask;
            await ethereumTask;
        }

        private void RemoveLogFiles(string logDirectory)
        {
            Console.WriteLine("Removing log files.");

            var logsDir = string.IsNullOrEmpty(logDirectory)
                ? Path.Combine(PathUtils.GetExecutingDirectory(), "logs")
                : logDirectory;
            if (!Directory.Exists(logsDir))
            {
                return;
            }

            var files = Directory.GetFiles(logsDir);
            foreach (string file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error removing log file: {file}, exp: {e}");
                }
            }
        }

        private static HiveEthereumRunner GetHiveRunner(IRpcModuleProvider rpcModuleProvider, IConfigProvider configProvider, ILogManager logManager)
        {
            var specProvider = RopstenSpecProvider.Instance;
            var difficultyCalculator = new DifficultyCalculator(specProvider);
            var sealEngine = new EthashSealEngine(new Ethash(logManager), difficultyCalculator, logManager);
            var logger = logManager.GetClassLogger();
            var initConfig = configProvider.GetConfig<IInitConfig>();
            var dbProvider = new MemDbProvider();

            var ethereumSigner = new EthereumSigner(specProvider, logManager);

            var transactionPool = new TransactionPool(
                new PersistentTransactionStorage(dbProvider.PendingTxsDb, specProvider),
                new PendingTransactionThresholdValidator(initConfig.ObsoletePendingTransactionInterval,
                    initConfig.RemovePendingTransactionInterval), new Timestamp(),
                ethereumSigner, logManager, initConfig.RemovePendingTransactionInterval,
                initConfig.PeerNotificationThreshold);

            var receiptStorage = new PersistentReceiptStorage(dbProvider.ReceiptsDb, specProvider);

            var blockTree = new BlockTree(
                dbProvider.BlocksDb,
                dbProvider.BlockInfosDb,
                specProvider,
                transactionPool,
                logManager);
            
            var rewardCalculator = new RewardCalculator(specProvider);

            var headerValidator = new HeaderValidator(
                blockTree,
                sealEngine,
                specProvider,
                logManager);

            var ommersValidator = new OmmersValidator(
                blockTree,
                headerValidator,
                logManager);

            var txValidator = new TransactionValidator(
                new SignatureValidator(specProvider.ChainId));

            var blockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                specProvider,
                logManager);

            var stateTree = new StateTree(dbProvider.StateDb);

            var stateProvider = new StateProvider(
                stateTree,
                dbProvider.CodeDb,
                logManager);

            var storageProvider = new StorageProvider(
                dbProvider.StateDb,
                stateProvider,
                logManager);

            var blockhashProvider = new BlockhashProvider(
                blockTree);

            var virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                logManager);

            var transactionProcessor = new TransactionProcessor(
                specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                logManager);

            var txRecoveryStep = new TxSignaturesRecoveryStep(ethereumSigner);
            var recoveryStep = (IBlockDataRecoveryStep) txRecoveryStep;

            var blockProcessor = new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                transactionProcessor,
                dbProvider.StateDb,
                dbProvider.CodeDb,
                stateProvider,
                storageProvider,
                transactionPool,
                receiptStorage,
                logManager);

            var blockchainProcessor = new BlockchainProcessor(
                blockTree,
                blockProcessor,
                recoveryStep,
                logManager,
                true);

            var jsonSerializer = new JsonSerializer(logManager);
            var mapper = new JsonRpcModelMapper();
            
            var transactionPoolInfoProvider = new TransactionPoolInfoProvider(stateProvider);

            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, blockProcessor, transactionPool, logManager);
            var wallet = new HiveWallet();

            var blockchainBridge = new BlockchainBridge(
                ethereumSigner,
                stateProvider,
                blockTree,
                transactionPool,
                transactionPoolInfoProvider,
                receiptStorage,
                filterStore,
                filterManager,
                wallet,
                transactionProcessor);
            
            var module = new EthModule(jsonSerializer, configProvider, mapper, logManager, blockchainBridge);
            rpcModuleProvider.Register<IEthModule>(module);

            return new HiveEthereumRunner(jsonSerializer, blockchainProcessor, blockTree,
                stateProvider, new StateDb(),
                logger, configProvider, specProvider, wallet);
        }
    }
}