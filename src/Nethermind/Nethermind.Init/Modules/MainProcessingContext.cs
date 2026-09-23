// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init.Modules;

public class MainProcessingContext : IMainProcessingContext, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler, IAsyncDisposable
{
    public MainProcessingContext(
        ILifetimeScope rootLifetimeScope,
        IInitConfig initConfig,
        IBlockValidationModule[] blockValidationModules,
        IMainProcessingModule[] mainProcessingModules,
        IWorldStateManager worldStateManager,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {

        IWorldStateScopeProvider worldState = worldStateManager.GlobalWorldState;
        if (logManager.GetClassLogger<WorldStateScopeOperationLogger>().IsTrace)
        {
            worldState = new WorldStateScopeOperationLogger(worldStateManager.GlobalWorldState, logManager);
        }

        worldState = new WorldStateMetricsScopeProvider(worldState, static time => Blockchain.Metrics.StateMerkleizationTime = time);

        ILifetimeScope innerScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder
                // These are main block processing specific
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddModule(blockValidationModules)
                .AddSingleton<BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler>(this)
                .AddModule(mainProcessingModules)

                .AddScoped<BlockchainProcessor.Options, IReceiptConfig, IBlocksConfig>((receiptConfig, blocksConfig) => new()
                {
                    StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                    DumpOptions = initConfig.AutoDump,
                    ProcessingCores = blocksConfig.ProcessingCores,
                    DedicatedProcessingThread = blocksConfig.DedicatedProcessingThread
                })
                .AddScoped<BlockchainProcessor>()
                .Bind<IBlockchainProcessor, BlockchainProcessor>()
                .Bind<IBlockProcessingQueue, BlockchainProcessor>()
                // And finally, to wrap things up.
                .AddScoped<Components>()
                ;
        });

        _components = innerScope.Resolve<Components>();

        if (initConfig.ExitOnInvalidBlock)
        {
            ILogger exitLogger = logManager.GetClassLogger<MainProcessingContext>();
            _components.BlockProcessingQueue.InvalidBlock += (_, _) =>
            {
                if (exitLogger.IsInfo) exitLogger.Info("Exiting on invalid block");
                processExitSource.Exit(ExitCodes.InvalidBlock);
            };
        }

        LifetimeScope = innerScope;
    }

    public async ValueTask DisposeAsync() => await LifetimeScope.DisposeAsync();

    private readonly Components _components;
    public ILifetimeScope LifetimeScope { get; init; }
    public IBlockchainProcessor BlockchainProcessor => _components.BlockchainProcessor;
    public IBlockProcessingQueue BlockProcessingQueue => _components.BlockProcessingQueue;
    public IWorldState WorldState => _components.WorldState;
    public IBranchProcessor BranchProcessor => _components.BranchProcessor;
    public IBlockProcessor BlockProcessor => _components.BlockProcessor;
    public ITransactionProcessor TransactionProcessor => _components.TransactionProcessor;
    public IGenesisLoader GenesisLoader => _components.GenesisLoader;
    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
    public void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs) => TransactionProcessed?.Invoke(this, txProcessedEventArgs);

    private record Components(
        ITransactionProcessor TransactionProcessor,
        IBranchProcessor BranchProcessor,
        IBlockProcessor BlockProcessor,
        IBlockchainProcessor BlockchainProcessor,
        IBlockProcessingQueue BlockProcessingQueue,
        IWorldState WorldState,
        IGenesisLoader GenesisLoader
    );
}
