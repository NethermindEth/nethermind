// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using static Nethermind.Consensus.Processing.BlockProcessor;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockValidationTransactionsExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider,
    bool validate,
    UInt256? blobBaseFeeOverride)
    : BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    protected override void EnhanceBlockExecutionContext(Block block, IReleaseSpec spec)
    {
        if (blobBaseFeeOverride is not null)
        {
            SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec, blobBaseFeeOverride.Value));
        }
    }

    protected override void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if (!validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        }

        base.ProcessTransaction(block, currentTx, index, receiptsTracer, processingOptions);
    }
}

public class SimulateReadOnlyBlocksProcessingEnv : IDisposable
{
    private IWorldState StateProvider { get; }
    public IBlockTree BlockTree { get; }
    public ISpecProvider SpecProvider { get; }

    private readonly IBlockValidator _blockValidator;
    private readonly ILogManager? _logManager;
    private readonly ITransactionProcessor _transactionProcessor;
    public IWorldState WorldState => StateProvider;

    public SimulateReadOnlyBlocksProcessingEnv(
        IWorldState worldState,
        IReadOnlyBlockTree baseBlockTree,
        IReadOnlyDbProvider readOnlyDbProvider,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ILogManager? logManager = null)
    {
        SpecProvider = specProvider;
        DbProvider = readOnlyDbProvider;
        _logManager = logManager;

        BlockTree = new BlockTreeOverlay(baseBlockTree, blockTree);
        StateProvider = worldState;
        SimulateBlockhashProvider blockhashProvider = new SimulateBlockhashProvider(new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager), BlockTree);
        CodeInfoRepository = new OverridableCodeInfoRepository(new CodeInfoRepository());
        SimulateVirtualMachine virtualMachine = new SimulateVirtualMachine(new VirtualMachine(blockhashProvider, specProvider, logManager));
        _transactionProcessor = new TransactionProcessor(SpecProvider, StateProvider, virtualMachine, CodeInfoRepository, _logManager);
        _blockValidator = CreateValidator();
        BlockTransactionPicker = new BlockProductionTransactionPicker(specProvider, ignoreEip3607: true);
    }

    private IReadOnlyDbProvider DbProvider { get; }
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

    public IBlockProcessor GetProcessor(bool validate, UInt256? blobBaseFeeOverride)
    {
        ITransactionProcessorAdapter adapter;
        if (validate)
        {
            adapter = new ExecuteTransactionProcessorAdapter(_transactionProcessor);
        }
        else
        {
            adapter = new TraceTransactionProcessorAdapter(_transactionProcessor);
        }

        return new BlockProcessor(
            SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            new SimulateBlockValidationTransactionsExecutor(
                adapter, StateProvider, validate,
                blobBaseFeeOverride),
            StateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(_transactionProcessor, StateProvider),
            new BlockhashStore(SpecProvider, StateProvider),
            _logManager,
            new WithdrawalProcessor(StateProvider, _logManager),
            new ExecutionRequestsProcessor(_transactionProcessor)
        );
    }
}
