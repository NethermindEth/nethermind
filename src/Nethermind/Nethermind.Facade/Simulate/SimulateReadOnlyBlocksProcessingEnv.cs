// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Consensus.Processing.BlockProcessor;

namespace Nethermind.Facade.Simulate;

public class SimulateReadOnlyBlocksProcessingEnv : ReadOnlyTxProcessingEnvBase, IDisposable
{
    private readonly ILogManager? _logManager;
    private readonly IBlockValidator _blockValidator;
    private readonly TransactionProcessor _transactionProcessor;
    public ISpecProvider SpecProvider { get; }
    public IWorldStateManager WorldStateManager { get; }
    public IVirtualMachine VirtualMachine { get; }
    public IReadOnlyDbProvider DbProvider { get; }
    public IReadOnlyBlockTree ReadOnlyBlockTree { get; set; }
    public OverridableCodeInfoRepository CodeInfoRepository { get; }
    public BlockProductionTransactionPicker BlockTransactionPicker { get; }

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

        BlockTree = new BlockTreeOverlay(ReadOnlyBlockTree, blockTree);
        BlockhashProvider = new SimulateBlockhashProvider(new BlockhashProvider(BlockTree, logManager), BlockTree);
        StateProvider = WorldStateManager.GlobalWorldState;
        StateReader = WorldStateManager.GlobalStateReader;
        CodeInfoRepository = new OverridableCodeInfoRepository(new CodeInfoRepository());
        VirtualMachine = new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager);
        _transactionProcessor = new TransactionProcessor(SpecProvider, StateProvider, VirtualMachine, CodeInfoRepository, _logManager, !doValidation);
        _blockValidator = CreateValidator();
        BlockTransactionPicker = new BlockProductionTransactionPicker(specProvider, true);

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
            new BlockProcessor.BlockValidationTransactionsExecutor(_transactionProcessor, StateProvider),
            StateProvider,
            NullReceiptStorage.Instance,
            NullWitnessCollector.Instance,
            _logManager);
    }

    public IReadOnlyTransactionProcessor Build(Hash256 stateRoot) => new ReadOnlyTransactionProcessor(_transactionProcessor, StateProvider, stateRoot);

    public void Dispose()
    {
        DbProvider.Dispose();
    }
}
