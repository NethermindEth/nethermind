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

public class WitnessGeneratingBlockProcessingEnv(
    ISpecProvider specProvider,
    IBlockTree blockTree,
    ISealValidator sealValidator,
    ILogManager logManager)
{
    private ITransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockFinder blockFinder)
    {
        var blockhashProvider = new BlockhashProvider(blockFinder, specProvider, state, logManager);
        var vm = new VirtualMachine(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor(specProvider, state, vm, new EthereumCodeInfoRepository(), logManager);
    }

    public (IBlockProcessor, WitnessCollector) CreateWitnessGeneratingBlockProcessor(WorldState baseWorldState)
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

        return (new BlockProcessor(
            specProvider,
            blockValidator,
            NoBlockRewards.Instance, // TODO: pass block rewards
            txExecutor,
            state,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(specProvider, state),
            logManager,
            new WithdrawalProcessor(state, logManager),
            new ExecutionRequestsProcessor(txProcessor)), new WitnessCollector(blockFinder, state));
    }
}
