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
    public ISpecProvider SpecProvider { get; }
    public IMultiCallVirtualMachine VirtualMachine { get; }

    //We need ability to get many instances that do not conflict in terms of editable tmp storage - thus we implement env cloning
    public static MultiCallReadOnlyBlocksProcessingEnv Create(bool traceTransfers, IReadOnlyDbProvider? readOnlyDbProvider,
        ISpecProvider? specProvider,
        ILogManager? logManager
        )
    {
        if (specProvider == null)
        {
            throw new ArgumentNullException(nameof(specProvider));
        }
        if (readOnlyDbProvider == null)
        {
            throw new ArgumentNullException(nameof(readOnlyDbProvider));
        }

        ReadOnlyDbProvider? dbProvider = new(readOnlyDbProvider, true);
        TrieStore trieStore = new(readOnlyDbProvider.StateDb, logManager);
        BlockTree blockTree = new(readOnlyDbProvider,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            logManager);

        return new MultiCallReadOnlyBlocksProcessingEnv(
            traceTransfers,
            dbProvider,
            trieStore,
            blockTree,
            specProvider,
            logManager);
    }

    public MultiCallReadOnlyBlocksProcessingEnv Clone(bool traceTransfers)
    {
        return Create(traceTransfers, DbProvider, SpecProvider, _logManager);
    }

    private MultiCallReadOnlyBlocksProcessingEnv(
        bool traceTransfers,
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

        if (traceTransfers)
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
        TransactionProcessor? transactionProcessor = new(SpecProvider, StateProvider, VirtualMachine, _logManager);

        return new BlockProcessor(SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, StateProvider),
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
