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
using Nethermind.State;
using static Nethermind.Consensus.Processing.BlockProcessor;

namespace Nethermind.Facade.Simulate;

public class SimulateRequestState
{
    public bool Validate { get; set; }
    public UInt256? BlobBaseFeeOverride { get; set; }
}

public class SimulateBlockValidationTransactionsExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider,
    SimulateRequestState simulateState)
    : BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    protected override void EnhanceBlockExecutionContext(Block block, IReleaseSpec spec)
    {
        if (simulateState.BlobBaseFeeOverride is not null)
        {
            SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec, simulateState.BlobBaseFeeOverride.Value));
        }
    }

    protected override void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if (!simulateState.BlobBaseFeeOverride.HasValue)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        }

        base.ProcessTransaction(block, currentTx, index, receiptsTracer, processingOptions);
    }
}

public class SimulateTransactionProcessorAdapter(ITransactionProcessor transactionProcessor, SimulateRequestState simulateRequestState) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        return simulateRequestState.Validate ? transactionProcessor.Execute(transaction, txTracer) : transactionProcessor.Trace(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
}

public class SimulateReadOnlyBlocksProcessingEnv : IDisposable
{
    private IWorldState StateProvider { get; }
    public IBlockTree BlockTree { get; }
    public ISpecProvider SpecProvider { get; }
    public SimulateRequestState SimulateRequestState { get; }
    public IBlockProcessor BlockProcessor { get; }

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
        SimulateRequestState = new SimulateRequestState();
        BlockProcessor = GetProcessor(SimulateRequestState);
    }

    private IReadOnlyDbProvider DbProvider { get; }
    public OverridableCodeInfoRepository CodeInfoRepository { get; }

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

    private IBlockProcessor GetProcessor(SimulateRequestState simulateState)
    {
        return new BlockProcessor(
            SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            new SimulateBlockValidationTransactionsExecutor(
                new SimulateTransactionProcessorAdapter(_transactionProcessor, simulateState), StateProvider, simulateState),
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
