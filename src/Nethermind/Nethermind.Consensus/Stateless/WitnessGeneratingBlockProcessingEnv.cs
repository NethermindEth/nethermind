// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnv
{
    WitnessCollector CreateWitnessCollector();
}

public class WitnessGeneratingBlockProcessingEnv(
    ISpecProvider specProvider,
    IStateReader stateReader,
    WorldState baseWorldState,
    WitnessCapturingTrieStore witnessCapturingTrieStore,
    IReadOnlyBlockTree blockTree,
    ISealValidator sealValidator,
    IRewardCalculator rewardCalculator,
    IHeaderStore headerStore,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnv
{
    private TransactionProcessor CreateTransactionProcessor(IWorldState state, WitnessGeneratingBlockFinder blockFinder)
    {
        BlockhashProvider blockhashProvider = new(blockFinder, state, logManager);
        VirtualMachine vm = new(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor(new BlobBaseFeeCalculator(), specProvider, state, vm, new EthereumCodeInfoRepository(state), logManager);
    }

    public WitnessCollector CreateWitnessCollector()
    {
        WitnessGeneratingWorldState state = new(stateReader, baseWorldState);
        WitnessGeneratingBlockFinder blockFinder = new(blockTree, new BlockhashCache(headerStore, logManager));
        TransactionProcessor txProcessor = CreateTransactionProcessor(state, blockFinder);
        IBlockProcessor.IBlockTransactionsExecutor txExecutor =
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor), state);

        IHeaderValidator headerValidator = new HeaderValidator(blockTree, sealValidator, specProvider, logManager);
        IBlockValidator blockValidator = new BlockValidator(new TxValidator(specProvider.ChainId), headerValidator,
            new UnclesValidator(blockTree, headerValidator, logManager), specProvider, logManager);

        BlockProcessor blockProcessor = new(
            specProvider,
            blockValidator,
            rewardCalculator,
            txExecutor,
            state,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(state),
            logManager,
            new WithdrawalProcessor(state, logManager),
            new ExecutionRequestsProcessor(txProcessor));

        return new WitnessCollector(blockFinder, state, witnessCapturingTrieStore, blockProcessor, specProvider);
    }
}
