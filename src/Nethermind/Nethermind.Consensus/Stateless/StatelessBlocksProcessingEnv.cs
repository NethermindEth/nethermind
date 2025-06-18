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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public class StatelessBlocksProcessingEnv(
    IWorldState worldState,
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public IBlockProcessor GetProcessor(Witness witness, Hash256 stateRoot)
    {
        WorldState state = new(GetTrie(witness), GetCodeDb(witness), logManager);
        state.StateRoot = stateRoot;
        ITransactionProcessor txProcessor = CreateTransactionProcessor(state);
        IBlockProcessor.IBlockTransactionsExecutor txExecutor =
            new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, state);

        return new BlockProcessor(
            specProvider,
            blockValidator,
            NoBlockRewards.Instance,
            txExecutor,
            state,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(specProvider, state),
            logManager,
            new WithdrawalProcessor(state, logManager),
            new ExecutionRequestsProcessor(txProcessor)
        );
    }

    private ITransactionProcessor CreateTransactionProcessor(IWorldState state)
    {
        var blockhashProvider = new BlockhashProvider(blockFinder, specProvider, state, logManager);
        var vm = new VirtualMachine(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor(specProvider, state, vm, new CodeInfoRepository(), logManager);
    }

    private ITrieStore GetTrie(Witness witness)
    {
        IKeyValueStore db = new MemDb();
        foreach (var stateElement in witness.State)
        {
            var hash = Keccak.Compute(stateElement);
            db.PutSpan(hash.Bytes, stateElement);
        }

        NodeStorage nodeStorage = new(db, INodeStorage.KeyScheme.Hash);
        return new TrieStore(nodeStorage, NoPruning.Instance, NoPersistence.Instance, new PruningConfig(), NullLogManager.Instance);
    }

    private IKeyValueStoreWithBatching GetCodeDb(Witness witness)
    {
        IKeyValueStoreWithBatching db = new MemDb();
        foreach (var code in witness.Codes)
        {
            var hash = Keccak.Compute(code).Bytes;
            db.PutSpan(hash, code);
        }
        return db;
    }

    public (IBlockProcessor, WitnessGeneratingWorldState) CreateWitnessGeneratingBlockProcessor()
    {
        WitnessGeneratingWorldState state = new(worldState);
        ITransactionProcessor txProcessor = CreateTransactionProcessor(state);
        IBlockProcessor.IBlockTransactionsExecutor txExecutor =
            new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, state);
        return (new BlockProcessor(
            specProvider,
            blockValidator,
            NoBlockRewards.Instance,
            txExecutor,
            state,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(specProvider, state),
            logManager,
            new WithdrawalProcessor(state, logManager),
            new ExecutionRequestsProcessor(txProcessor)), state);
    }
}
