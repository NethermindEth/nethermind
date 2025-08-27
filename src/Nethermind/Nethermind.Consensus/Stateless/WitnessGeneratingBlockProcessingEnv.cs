// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
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

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnv
{
    WitnessCollector CreateWitnessCollector();
}

public class WitnessGeneratingBlockProcessingEnv(
    ISpecProvider specProvider,
    WorldState baseWorldState,
    IReadOnlyBlockTree blockTree,
    ISealValidator sealValidator,
    IRewardCalculator rewardCalculator,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnv
{
    private ITransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockFinder blockFinder)
    {
        var blockhashProvider = new BlockhashProvider(blockFinder, specProvider, state, logManager);
        var vm = new VirtualMachine(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor(specProvider, state, vm, new EthereumCodeInfoRepository(), logManager);
    }

    public WitnessCollector CreateWitnessCollector()
    {
        WitnessGeneratingWorldState state = new(baseWorldState);
        WitnessGeneratingBlockFinder blockFinder = new(blockTree);
        ITransactionProcessor txProcessor = CreateTransactionProcessor(state, blockFinder);
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
            new BlockhashStore(specProvider, state),
            logManager,
            new WithdrawalProcessor(state, logManager),
            new ExecutionRequestsProcessor(txProcessor));
        return new WitnessCollector(blockFinder, state, blockProcessor, specProvider);
    }
}
