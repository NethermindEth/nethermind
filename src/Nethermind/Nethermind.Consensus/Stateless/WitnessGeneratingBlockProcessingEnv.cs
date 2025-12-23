// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
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
    IBlockhashProvider mainBlockhashProvider,
    ISealValidator sealValidator,
    IRewardCalculator rewardCalculator,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnv
{
    private ITransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockhashProvider blockhashProvider)
    {
        var vm = new VirtualMachine(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor<EthereumGasPolicy>(BlobBaseFeeCalculator.Instance, specProvider, state, vm, new EthereumCodeInfoRepository(state), logManager);
    }

    public WitnessCollector CreateWitnessCollector()
    {
        WitnessGeneratingWorldState state = new(baseWorldState);
        WitnessGeneratingBlockHashProvider blockHashProvider = new(mainBlockhashProvider, blockTree);
        ITransactionProcessor txProcessor = CreateTransactionProcessor(state, blockHashProvider);
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
        return new WitnessCollector(blockHashProvider, state, blockProcessor, specProvider);
    }
}
