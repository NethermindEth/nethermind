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

public class MultiCallReadOnlyBlocksProcessingEnv : ReadOnlyTxProcessingEnvBase, IDisposable
{
    private readonly ITrieStore? _trieStore;
    private readonly ILogManager? _logManager;
    private readonly IBlockValidator _blockValidator;
    private readonly InMemoryReceiptStorage _receiptStorage;
    public ITransactionProcessor TransactionProcessor { get; }
    public ISpecProvider SpecProvider { get; }
    public IMultiCallVirtualMachine VirtualMachine { get; }
    public bool TraceTransfers { get; set; }

    //We need ability to get many instances that do not conflict in terms of editable tmp storage - thus we implement env cloning
    public static MultiCallReadOnlyBlocksProcessingEnv Create(bool TraceTransfers, IReadOnlyDbProvider? readOnlyDbProvider,
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
            TraceTransfers,
            DbProvider,
            trieStore,
            BlockTree,
            specProvider,
            logManager);
    }

    public MultiCallReadOnlyBlocksProcessingEnv Clone(bool TraceTransfers)
    {
        return Create(TraceTransfers, DbProvider, SpecProvider, _logManager);
    }

    private MultiCallReadOnlyBlocksProcessingEnv(
        bool TraceTransfers,
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

        if (TraceTransfers)
        {
            VirtualMachine = new MultiCallVirtualMachine<MultiCallDoTraceTransfers>(BlockhashProvider, specProvider, logManager);
        }
        else
        {
            VirtualMachine = new MultiCallVirtualMachine<MultiCallDoNotTraceTransfers>(BlockhashProvider, specProvider, logManager);
        }

        HeaderValidator headerValidator = new(
            BlockTree,
            Always.Valid,
            SpecProvider,
            _logManager);

        BlockValidator? blockValidator = new(
            new TxValidator(SpecProvider.ChainId),
            headerValidator,
            Always.Valid,
            SpecProvider,
            _logManager);

        _blockValidator = new MultiCallBlockValidatorProxy(blockValidator);
    }

    public IBlockProcessor GetProcessor()
    {
        var yransactionProcessor = new TransactionProcessor(SpecProvider, StateProvider, VirtualMachine, _logManager);

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
