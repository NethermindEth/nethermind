// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

public class StatelessBlockProcessingEnv(
    Witness witness,
    ISpecProvider specProvider,
    ISealValidator sealValidator,
    ILogManager logManager)
{
    private IBlockProcessor? _blockProcessor;
    private IWorldState? _worldState;
    // Per-env (i.e. per-block) code cache: within one block it avoids re-reading code and
    // re-running jump-destination analysis on every CALL, while the first fetch of each code hash
    // still reads through the world state, so witness-completeness checks keep firing. The
    // process-wide StaticCodeCache.Instance must not be used here: it would leak code across
    // blocks and mask deliberately missing witness code (negative validation tests).
    private readonly StaticCodeCache _codeCache = new();

    public IBlockProcessor BlockProcessor => _blockProcessor ??= GetProcessor();

    public IWorldState WorldState => _worldState ??= new StatelessExecutingWorldState(
        new WorldState(
            new TrieStoreScopeProvider(
                new RawTrieStore(witness.CreateNodeStorage()), witness.CreateCodeDb(), logManager
            ),
            logManager
        )
    );

    private BlockProcessor GetProcessor()
    {
        using ArrayPoolList<BlockHeader> readOnlyCollection = witness.DecodeHeaders();
        StatelessBlockTree statelessBlockTree = new(readOnlyCollection);
        BlockhashProvider blockhashProvider = new(statelessBlockTree, WorldState, logManager);
        EthereumTransactionProcessor txProcessor = CreateTransactionProcessor(WorldState, blockhashProvider);
        BlockAccessListManager blockAccessListManager = new(
            WorldState,
            specProvider,
            blockhashProvider,
            logManager,
            new BlocksConfig()
            {
                ParallelExecution = false,
                ParallelExecutionBatchRead = false
            },
            new WithdrawalProcessorFactory(logManager),
            codeInfoRepositoryFactory: state => new CacheCodeInfoRepository(state, new EthereumPrecompileProvider(), _codeCache),
            executionRequestsProcessorFactory: StatelessExecutionRequestsProcessorFactory.Instance
        );
        BlockProcessor.ParallelBlockValidationTransactionsExecutor txExecutor = new(
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor),
                WorldState
            ),
            WorldState,
            specProvider,
            blockAccessListManager,
            logManager
        );

        HeaderValidator headerValidator = new(statelessBlockTree, sealValidator, specProvider, logManager);
        BlockValidator blockValidator = new(
            new TxValidator(specProvider.ChainId),
            headerValidator,
            new UnclesValidator(statelessBlockTree, headerValidator, logManager),
            specProvider,
            logManager
        );

        return new BlockProcessor(
            specProvider,
            blockValidator,
            NoBlockRewards.Instance,
            txExecutor,
            WorldState,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, WorldState),
            new BlockhashStore(WorldState),
            logManager,
            new WithdrawalProcessor(WorldState, logManager),
            new StatelessExecutionRequestsProcessor(txProcessor),
            blockAccessListManager
        );
    }

    private EthereumTransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockhashProvider blockhashProvider)
        => new(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            state,
            new EthereumVirtualMachine(blockhashProvider, specProvider, logManager),
            new CacheCodeInfoRepository(state, new EthereumPrecompileProvider(), _codeCache),
            logManager
        );
}
