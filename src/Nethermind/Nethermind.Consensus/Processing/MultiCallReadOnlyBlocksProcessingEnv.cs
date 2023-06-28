// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Processing;

public class MultiCallReadOnlyBlocksProcessingEnv : ReadOnlyTxProcessingEnvBase, IMultiCallBlocksProcessingEnv
{
    private readonly ITrieStore? _trieStore;
    private readonly ILogManager? _logManager;
    private readonly IBlockValidator _blockValidator;
    private readonly InMemoryReceiptStorage _receiptStorage;
    public ITransactionProcessor TransactionProcessor { get; }
    public ISpecProvider SpecProvider { get; }
    public MultiCallVirtualMachine Machine { get; }

    //We need abilety to get many instances that do not conflict in terms of editable tmp storage - thus we implement env cloning
    public static IMultiCallBlocksProcessingEnv Create(IReadOnlyDbProvider? readOnlyDbProvider,
        ISpecProvider? specProvider,
        ILogManager? logManager
        )
    {
        ReadOnlyDbProvider? DbProvider = new(readOnlyDbProvider, true);
        TrieStore trieStore = new(readOnlyDbProvider.StateDb, logManager);
        BlockTree BlockTree = new(readOnlyDbProvider,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            logManager);

        return new MultiCallReadOnlyBlocksProcessingEnv(
            DbProvider,
            trieStore,
            BlockTree,
            specProvider,
            logManager);
    }

    public IMultiCallBlocksProcessingEnv Clone()
    {
        return Create(DbProvider, SpecProvider, _logManager);
    }

    private MultiCallReadOnlyBlocksProcessingEnv(
        IReadOnlyDbProvider? readOnlyDbProvider,
        ITrieStore? trieStore,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager) : base(readOnlyDbProvider, trieStore, blockTree,
        logManager)
    {
       
        _trieStore = trieStore;
        _logManager = logManager;
        SpecProvider = specProvider;


        _receiptStorage = new InMemoryReceiptStorage();

        Machine = new MultiCallVirtualMachine(BlockhashProvider, specProvider, logManager);

        HeaderValidator headerValidator = new(
            BlockTree,
            Always.Valid,
            SpecProvider,
            _logManager);

        _blockValidator = new MultiCallBlockValidator(new TxValidator(SpecProvider.ChainId),
            headerValidator,
            Always.Valid,
            SpecProvider,
            _logManager);

    }

    public IBlockProcessor GetProcessor(Keccak stateRoot)
    {
        var yransactionProcessor = new TransactionProcessor(SpecProvider, StateProvider, Machine, _logManager);

        return new BlockProcessor(SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(yransactionProcessor, StateProvider),
            StateProvider,
            _receiptStorage,
            NullWitnessCollector.Instance,
            _logManager);
    }

    public void Dispose()
    {
        _trieStore?.Dispose();
        DbProvider.Dispose();
    }
}
