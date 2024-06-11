// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Consensus.Processing.BlockProcessor;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockValidationTransactionsExecutor : BlockValidationTransactionsExecutor
{
    public SimulateBlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IWorldState stateProvider) : base(transactionProcessor, stateProvider)
    {
    }

    public SimulateBlockValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor, IWorldState stateProvider) : base(transactionProcessor, stateProvider)
    {
    }

    protected override void ProcessTransaction(in BlockExecutionContext blkCtx, Transaction currentTx, int index,
        BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        base.ProcessTransaction(in blkCtx, currentTx, index, receiptsTracer, processingOptions);
    }
}

public class SimulateReadOnlyBlocksProcessingEnv : IDisposable
{
    private readonly IBlockValidator _blockValidator;
    private readonly ILogManager? _logManager;
    private readonly TransactionProcessor _transactionProcessor;

    public SimulateReadOnlyBlocksProcessingEnv(
        IWorldStateManager worldStateManager,
        IReadOnlyBlockTree baseBlockTree,
        IReadOnlyDbProvider readOnlyDbProvider,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ILogManager? logManager = null,
        bool validate = false)
    {
        ReadOnlyBlockTree = baseBlockTree;
        DbProvider = readOnlyDbProvider;
        WorldStateManager = worldStateManager;
        _logManager = logManager;

        BlockTree = new BlockTreeOverlay(ReadOnlyBlockTree, blockTree);
        WorldState = WorldStateManager.GlobalWorldState;
        StateReader = WorldStateManager.GlobalStateReader;

        SpecProvider = specProvider;
        BlockhashProvider = new SimulateBlockhashProvider(new BlockhashProvider(BlockTree, specProvider, WorldState, logManager), BlockTree);
        CodeInfoRepository = new OverridableCodeInfoRepository(new CodeInfoRepository());
        VirtualMachine = new SimulateVirtualMachine(new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager));
        _transactionProcessor = new SimulateTransactionProcessor(specProvider, WorldState, VirtualMachine, CodeInfoRepository, _logManager, validate);
        _blockValidator = CreateValidator();
        BlockTransactionPicker = new BlockProductionTransactionPicker(specProvider, true);
    }

    public IWorldState WorldState { get; }
    public IStateReader StateReader { get; }
    public IBlockTree BlockTree { get; }
    private IBlockhashProvider BlockhashProvider { get; }
    public ISpecProvider SpecProvider { get; }
    private IWorldStateManager WorldStateManager { get; }
    private IVirtualMachine VirtualMachine { get; }
    private IReadOnlyDbProvider DbProvider { get; }
    private IReadOnlyBlockTree ReadOnlyBlockTree { get; }
    public OverridableCodeInfoRepository CodeInfoRepository { get; }
    public BlockProductionTransactionPicker BlockTransactionPicker { get; }

    public void Dispose()
    {
        DbProvider.Dispose();
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

    public IBlockProcessor GetProcessor(bool validate) =>
        new BlockProcessor(SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            validate
                ? new BlockValidationTransactionsExecutor(_transactionProcessor, WorldState)
                : new SimulateBlockValidationTransactionsExecutor(_transactionProcessor, WorldState),
            WorldState,
            NullReceiptStorage.Instance,
            new BlockhashStore(BlockTree, SpecProvider, WorldState),
            _logManager);
}
