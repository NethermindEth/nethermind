// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Consensus.Processing.BlockProcessor;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockValidationTransactionsExecutor(
    ITransactionProcessor transactionProcessor,
    bool validate,
    UInt256? blobBaseFeeOverride)
    : BlockValidationTransactionsExecutor(transactionProcessor)
{
    protected override BlockExecutionContext CreateBlockExecutionContext(Block block) =>
        blobBaseFeeOverride is not null ? new BlockExecutionContext(block.Header, blobBaseFeeOverride.Value) : base.CreateBlockExecutionContext(block);

    protected override void ProcessTransaction(IWorldState worldState, in BlockExecutionContext blkCtx,
        Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if (!validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        }

        base.ProcessTransaction(worldState, in blkCtx, currentTx, index, receiptsTracer, processingOptions);
    }
}

public class SimulateReadOnlyBlocksProcessingEnv : ReadOnlyTxProcessingEnvBase, IDisposable
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
        : base(worldStateManager, blockTree, specProvider, logManager)
    {
        ReadOnlyBlockTree = baseBlockTree;
        DbProvider = readOnlyDbProvider;
        _logManager = logManager;

        BlockTree = new BlockTreeOverlay(ReadOnlyBlockTree, blockTree);
        BlockhashProvider = new SimulateBlockhashProvider(new BlockhashProvider(BlockTree, specProvider, logManager), BlockTree);
        CodeInfoRepository = new OverridableCodeInfoRepository(new CodeInfoRepository());
        VirtualMachine = new SimulateVirtualMachine(new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager));
        _transactionProcessor = new SimulateTransactionProcessor(SpecProvider, VirtualMachine, CodeInfoRepository, _logManager, validate);
        _blockValidator = CreateValidator();
        BlockTransactionPicker = new BlockProductionTransactionPicker(specProvider, true);
    }

    public IVirtualMachine VirtualMachine { get; }
    public IReadOnlyDbProvider DbProvider { get; }
    public IReadOnlyBlockTree ReadOnlyBlockTree { get; set; }
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

    public IBlockProcessor GetProcessor(bool validate, UInt256? blobBaseFeeOverride) =>
        new BlockProcessor(SpecProvider,
            _blockValidator,
            NoBlockRewards.Instance,
            new SimulateBlockValidationTransactionsExecutor(_transactionProcessor, validate, blobBaseFeeOverride),
            WorldStateProvider,
            NullReceiptStorage.Instance,
            new BlockhashStore(SpecProvider),
            new BeaconBlockRootHandler(_transactionProcessor),
            _logManager);
}
