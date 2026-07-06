// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Repositories;

namespace Nethermind.Facade.Simulate;

public class SimulateReadOnlyBlocksProcessingEnvFactory(
    IProcessingEnvBuilder envBuilder,
    IReadOnlyBlockTree baseBlockTree,
    IDbProvider dbProvider,
    ISpecProvider specProvider,
    ILogManager? logManager = null) : ISimulateReadOnlyBlocksProcessingEnvFactory
{
    public ISimulateReadOnlyBlocksProcessingEnv Create()
    {
        IReadOnlyDbProvider editableDbProvider = new ReadOnlyDbProvider(dbProvider, true);

        IHeaderStore mainHeaderStore = new HeaderStore(editableDbProvider.HeadersDb, editableDbProvider.BlockNumbersDb, (IHeaderDecoder)Rlp.GetDecoderOrThrow<BlockHeader>());
        SimulateDictionaryHeaderStore tmpHeaderStore = new(mainHeaderStore);

        IBlockAccessListStore mainBalStore = new BlockAccessListStore(editableDbProvider.BlockAccessListDb);

        BlockTree tempBlockTree = CreateTempBlockTree(editableDbProvider, specProvider, logManager, editableDbProvider, tmpHeaderStore, mainBalStore);
        BlockTreeOverlay overrideBlockTree = new(baseBlockTree, tempBlockTree);

        IEnv env = envBuilder
            .WithOverridableEnv() // worldstate related override here
            .ThatDisposes(editableDbProvider)
            .WithComponent<IReadOnlyDbProvider>(editableDbProvider)
            .WithComponent<IBlockTree>(overrideBlockTree)
            .WithComponent<BlockTreeOverlay>(overrideBlockTree)
            .WithComponent<IHeaderStore>(tmpHeaderStore)
            .WithReplacedComponent<IHeaderFinder>(c => c.Resolve<IHeaderStore>())
            .WithReplacedComponent<IBlockhashCache, BlockhashCache>()
            .WithBlockValidationConfiguration()
            .WithReplacedComponent<IUnresolvedBlockhashPolicy, NullUnresolvedBlockhashPolicy>()
            // Decorators and Bind have no With* equivalent, so they stay in Configure.
            .Configure((builder) => builder
                .AddDecorator<IBlockhashProvider, SimulateBlockhashProvider>()
                .AddDecorator<IBlockValidator, SimulateBlockValidatorProxy>()
                .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeOverrideCalculatorDecorator>()
                .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, SimulateBlockValidationTransactionsExecutor>())
            .WithReplacedComponent<ITransactionProcessorAdapter, SimulateTransactionProcessorAdapter>()
            .WithComponent<IReceiptStorage>(NullReceiptStorage.Instance)
            .WithComponent<SimulateRequestState>()
            .Configure((builder) => builder.BindScoped<IBlobBaseFeeOverrideProvider, SimulateRequestState>())
            .WithComponent<SimulateReadOnlyBlocksProcessingEnv>()
            .OwnedByParentLifetime()
            .BuildAs<IEnv>();

        return env.Env;
    }

    // The built scope is owned by the parent lifetime; the wrapper only surfaces the resolved env.
    public interface IEnv
    {
        SimulateReadOnlyBlocksProcessingEnv Env { get; }
    }

    private static BlockTree CreateTempBlockTree(
        IReadOnlyDbProvider readOnlyDbProvider,
        ISpecProvider? specProvider,
        ILogManager logManager,
        IReadOnlyDbProvider editableDbProvider,
        SimulateDictionaryHeaderStore tmpHeaderStore,
        IBlockAccessListStore tmpBalStore)
    {
        IBlockStore mainBlockStore = new BlockStore(editableDbProvider.BlocksDb);
        const int badBlocksStored = 1;

        SimulateDictionaryBlockStore tmpBlockStore = new(mainBlockStore);
        IBadBlockStore badBlockStore = new BadBlockStore(editableDbProvider.BadBlocksDb, badBlocksStored);

        return new(tmpBlockStore,
            tmpHeaderStore,
            editableDbProvider.BlockInfosDb,
            editableDbProvider.MetadataDb,
            badBlockStore,
            tmpBalStore,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            new SyncConfig(),
            NullStateBoundary.Instance,
            new BlockTreeLogHider(logManager));
    }

    private class BlockTreeLogHider(ILogManager baseLogManager) : ILogManager
    {
        public ILogger GetClassLogger<T>()
        {
            if (typeof(T) != typeof(BlockTree))
            {
                return baseLogManager.GetClassLogger<T>();
            }

            // If not debug, hide all log
            ILogger baseLogger = baseLogManager.GetClassLogger<T>();
            return !baseLogger.IsDebug ? NullLogger.Instance : baseLogger;
        }

        public ILogger GetLogger(string loggerName) => baseLogManager.GetLogger(loggerName);
    }
}
