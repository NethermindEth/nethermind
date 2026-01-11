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
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnv
{
    IWitnessCollector CreateWitnessCollector();
}

public class WitnessGeneratingBlockProcessingEnv(
    ISpecProvider specProvider,
    WorldState baseWorldState,
    WitnessCapturingTrieStore witnessCapturingTrieStore,
    IReadOnlyBlockTree blockTree,
    ISealValidator sealValidator,
    IRewardCalculator rewardCalculator,
    IHeaderStore headerStore,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnv
{
    private TransactionProcessor<EthereumGasPolicy> CreateTransactionProcessor(IWorldState state, IHeaderFinder witnessGeneratingHeaderFinder)
    {
        BlockhashProvider blockhashProvider = new(new BlockhashCache(witnessGeneratingHeaderFinder, logManager), state, logManager);
        VirtualMachine vm = new(blockhashProvider, specProvider, logManager);
        ICodeInfoRepository codeInfoRepository = new CodeInfoRepository(state, new EthereumPrecompileProvider());
        return new TransactionProcessor<EthereumGasPolicy>(new BlobBaseFeeCalculator(), specProvider, state, vm, codeInfoRepository, logManager);
    }

    public IWitnessCollector CreateWitnessCollector()
    {
        WitnessGeneratingWorldState state = new(baseWorldState);
        WitnessGeneratingHeaderFinder witnessGenHeaderFinder = new(headerStore);
        TransactionProcessor<EthereumGasPolicy> txProcessor = CreateTransactionProcessor(state, witnessGenHeaderFinder);
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

        return new WitnessCollector(witnessGenHeaderFinder, state, witnessCapturingTrieStore, blockProcessor, specProvider);
    }
}
