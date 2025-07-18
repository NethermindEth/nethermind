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
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public class StatelessBlockProcessingEnv(
    ISpecProvider specProvider,
    ISealValidator sealValidator,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public IBlockProcessor GetProcessor(Witness witness)
    {
        WorldState state = new(CreateTrie(witness), CreateCodeDb(witness), logManager);
        state.StateRoot = witness.DecodedHeaders[0].StateRoot;
        IBlockTree statelessBlockTree = new StatelessBlockTree(witness.DecodedHeaders);
        ITransactionProcessor txProcessor = CreateTransactionProcessor(state, statelessBlockTree);
        IBlockProcessor.IBlockTransactionsExecutor txExecutor =
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor),
                state);

        IHeaderValidator headerValidator = new HeaderValidator(statelessBlockTree, sealValidator, specProvider, logManager);
        IBlockValidator blockValidator = new BlockValidator(new TxValidator(specProvider.ChainId), headerValidator,
            new UnclesValidator(statelessBlockTree, headerValidator, logManager), specProvider, logManager);

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

    private ITrieStore CreateTrie(Witness witness)
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

    private IKeyValueStoreWithBatching CreateCodeDb(Witness witness)
    {
        IKeyValueStoreWithBatching db = new MemDb();
        foreach (var code in witness.Codes)
        {
            var hash = Keccak.Compute(code).Bytes;
            db.PutSpan(hash, code);
        }
        return db;
    }

    private ITransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockFinder blockFinder)
    {
        var blockhashProvider = new BlockhashProvider(blockFinder, specProvider, state, logManager);
        var vm = new VirtualMachine(blockhashProvider, specProvider, logManager);
        return new TransactionProcessor(specProvider, state, vm, new EthereumCodeInfoRepository(), logManager);
    }
}
