// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
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

namespace Nethermind.Facade.Simulate;

public class SimulateReadOnlyBlocksProcessingEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyBlockTree baseBlockTree,
    IDbProvider dbProvider,
    ISpecProvider specProvider,
    ILogManager? logManager = null)
{
    public SimulateReadOnlyBlocksProcessingEnv Create(bool traceTransfers, bool validate)
    {
        IReadOnlyDbProvider editableDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        OverlayTrieStore overlayTrieStore = new(editableDbProvider.StateDb, worldStateManager.TrieStore, logManager);
        OverlayWorldStateManager overlayWorldStateManager = new(editableDbProvider, overlayTrieStore, logManager);
        BlockTree tempBlockTree = CreateTempBlockTree(editableDbProvider, specProvider, logManager, editableDbProvider);

        return new SimulateReadOnlyBlocksProcessingEnv(
            traceTransfers,
            overlayWorldStateManager,
            baseBlockTree,
            editableDbProvider,
            tempBlockTree,
            specProvider,
            logManager,
            validate);
    }

    private static BlockTree CreateTempBlockTree(IReadOnlyDbProvider readOnlyDbProvider, ISpecProvider? specProvider, ILogManager? logManager, IReadOnlyDbProvider editableDbProvider)
    {
        IBlockStore blockStore = new BlockStore(editableDbProvider.BlocksDb);
        IHeaderStore headerStore = new HeaderStore(editableDbProvider.HeadersDb, editableDbProvider.BlockNumbersDb);
        const int badBlocksStored = 1;
        IBlockStore badBlockStore = new BlockStore(editableDbProvider.BadBlocksDb, badBlocksStored);

        return new(blockStore,
            headerStore,
            editableDbProvider.BlockInfosDb,
            editableDbProvider.MetadataDb,
            badBlockStore,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            logManager);
    }
}


public class SimulateReadOnlyBlocksProcessingEnv : ReadOnlyTxProcessingEnvBase, IDisposable
{
    private readonly ILogManager? _logManager;
    private readonly IBlockValidator _blockValidator;
    private readonly bool _doValidation = false;
    private readonly TransactionProcessor _transactionProcessor;
    public ISpecProvider SpecProvider { get; }
    public IWorldStateManager WorldStateManager { get; }
    public IVirtualMachine VirtualMachine { get; }
    public IReadOnlyDbProvider DbProvider { get; }
    public IReadOnlyBlockTree ReadOnlyBlockTree { get; set; }
    public OverridableCodeInfoRepository CodeInfoRepository { get; }

    public SimulateReadOnlyBlocksProcessingEnv(
        bool traceTransfers,
        IWorldStateManager worldStateManager,
        IReadOnlyBlockTree baseBlockTree,
        IReadOnlyDbProvider readOnlyDbProvider,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ILogManager? logManager = null,
        bool doValidation = false)
        : base(worldStateManager, blockTree, logManager)
    {
        ReadOnlyBlockTree = baseBlockTree;
        DbProvider = readOnlyDbProvider;
        WorldStateManager = worldStateManager;
        _logManager = logManager;
        SpecProvider = specProvider;
        _doValidation = doValidation;

        BlockTree = new NonDistructiveBlockTreeOverlay(ReadOnlyBlockTree, blockTree);
        BlockHashProvider = new SimulateBlockHashProvider(new BlockHashProvider(BlockTree, logManager), BlockTree);

        //var store = new TrieStore(DbProvider.StateDb, LimboLogs.Instance);
        //StateProvider = new WorldState(store, DbProvider.CodeDb, LimboLogs.Instance);
        StateProvider = WorldStateManager.GlobalWorldState;
        StateReader = WorldStateManager.GlobalStateReader;

        CodeInfoRepository = new OverridableCodeInfoRepository(new CodeInfoRepository());

        VirtualMachine = new VirtualMachine(BlockHashProvider, specProvider, CodeInfoRepository, logManager);
        _transactionProcessor = new TransactionProcessor(SpecProvider, StateProvider, VirtualMachine, CodeInfoRepository, _logManager, !_doValidation);

        _blockValidator = CreateValidator();
    }

    private SimulateBlockValidatorProxy CreateValidator()
    {
        HeaderValidator headerValidator = new(
            BlockTree,
            Always.Valid,
            SpecProvider,
            _logManager);

        BlockValidator blockValidator = new(
            new TxValidator(SpecProvider!.ChainId),
            headerValidator,
            Always.Valid,
            SpecProvider,
            _logManager);

        return new SimulateBlockValidatorProxy(blockValidator);
    }

    public IBlockProcessor GetProcessor(Hash256 stateRoot)
    {
        return new BlockProcessor(SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(Build(stateRoot), StateProvider),
            StateProvider,
            NullReceiptStorage.Instance,
            NullWitnessCollector.Instance,
            _logManager);
    }

    public IReadOnlyTransactionProcessor Build(Hash256 stateRoot) => new ReadOnlyTransactionProcessor(_transactionProcessor, StateProvider, stateRoot);

    public void Dispose()
    {
        //_trieStore.Dispose();
        DbProvider.Dispose();
    }
}
