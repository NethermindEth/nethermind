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
    public IBlockProcessor BlockProcessor
    {
        get => _blockProcessor ??= GetProcessor();
    }

    private IWorldState? _worldState;
    public IWorldState WorldState
    {
        get => _worldState ??= new WorldState(
            new RawTrieStore(witness.NodeStorage),
            witness.CodeDb, logManager);
    }

    private IBlockProcessor GetProcessor()
    {
        StatelessBlockTree statelessBlockTree = new(witness.DecodedHeaders);
        ITransactionProcessor txProcessor = CreateTransactionProcessor(WorldState, statelessBlockTree);
        IBlockProcessor.IBlockTransactionsExecutor txExecutor =
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor),
                WorldState);

        IHeaderValidator headerValidator = new HeaderValidator(statelessBlockTree, sealValidator, specProvider, logManager);
        IBlockValidator blockValidator = new BlockValidator(new TxValidator(specProvider.ChainId), headerValidator,
            new UnclesValidator(statelessBlockTree, headerValidator, logManager), specProvider, logManager);

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
            new ExecutionRequestsProcessor(txProcessor)
        );
    }


    private ITransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockhashCache blockhashCache)
    {
        BlockhashProvider blockhashProvider = new(blockhashCache, state, logManager);
        VirtualMachine vm = new(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor(BlobBaseFeeCalculator.Instance, specProvider, state, vm, new EthereumCodeInfoRepository(state), logManager);
    }
}
