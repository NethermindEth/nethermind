// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Builds the per-worker <see cref="ITransactionProcessor"/> + <see cref="ITransactionProcessorAdapter"/>
/// pairs for <see cref="BlockAccessListManager"/>'s tx-processor pools.
/// </summary>
/// <remarks>
/// Bundles the construction-only dependencies so the manager and its pools carry one factory instead of
/// five loose ones. Resolved from the processing scope, it picks up that scope's overrides (a decorated
/// <see cref="ITransactionProcessorFactory"/>, scope-specific <see cref="CodeInfoRepositoryFactory"/>,
/// <see cref="TransactionProcessorAdapterFactory"/> and a decorated
/// <see cref="ITransactionProcessor.IBlobBaseFeeCalculator"/>) automatically. The optional parameters default to the
/// standard Ethereum implementations only at manual construction sites (stateless envs, tests) — in
/// container scopes the registered services are always injected.
/// </remarks>
/// <param name="frameCodeRunner">Attached to every machine the factory creates, as the container attaches it to the
/// scope's <see cref="IVirtualMachine"/>: a block with block-level access lists runs its transactions and system calls
/// on these machines rather than on that one.</param>
[method: Experimental("NMEVM0001")]
public class BalTxProcessorFactory(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    ILogManager logManager,
    CodeInfoRepositoryFactory? codeInfoRepositoryFactory,
    TransactionProcessorAdapterFactory? txProcessorAdapterFactory,
    ITransactionProcessorFactory? transactionProcessorFactory,
    ITransactionProcessor.IBlobBaseFeeCalculator? blobBaseFeeCalculator,
    FrameCodeRunner<EthereumGasPolicy>? frameCodeRunner)
{
    /// <summary>A factory whose machines run without a frame code runner.</summary>
    public BalTxProcessorFactory(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager,
        CodeInfoRepositoryFactory? codeInfoRepositoryFactory = null,
        TransactionProcessorAdapterFactory? txProcessorAdapterFactory = null,
        ITransactionProcessorFactory? transactionProcessorFactory = null,
        ITransactionProcessor.IBlobBaseFeeCalculator? blobBaseFeeCalculator = null)
        : this(blockHashProvider, specProvider, logManager, codeInfoRepositoryFactory, txProcessorAdapterFactory,
            transactionProcessorFactory, blobBaseFeeCalculator, frameCodeRunner: null)
    {
    }

    private static readonly CodeInfoRepositoryFactory DefaultCodeInfoRepositoryFactory =
        static worldState => new EthereumCodeInfoRepository(worldState);
    private static readonly TransactionProcessorAdapterFactory DefaultTxProcessorAdapterFactory =
        static txProcessor => new ExecuteTransactionProcessorAdapter(txProcessor);

    private readonly CodeInfoRepositoryFactory _codeInfoRepositoryFactory = codeInfoRepositoryFactory ?? DefaultCodeInfoRepositoryFactory;
    private readonly TransactionProcessorAdapterFactory _txProcessorAdapterFactory = txProcessorAdapterFactory ?? DefaultTxProcessorAdapterFactory;
    private readonly ITransactionProcessorFactory _transactionProcessorFactory = transactionProcessorFactory ?? new TransactionProcessorFactory<EthereumGasPolicy>();
    private readonly ITransactionProcessor.IBlobBaseFeeCalculator _blobBaseFeeCalculator = blobBaseFeeCalculator ?? BlobBaseFeeCalculator.Instance;

    /// <remarks>The caller owns the returned virtual machine and disposes it once it stops using the processor.</remarks>
    public (ITransactionProcessor Processor, ITransactionProcessorAdapter Adapter, IDisposable VirtualMachine) Create(IWorldState worldState, bool parallel)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager, frameCodeRunner);
        ITransactionProcessor processor = _transactionProcessorFactory.Create(
            _blobBaseFeeCalculator, specProvider, worldState, virtualMachine,
            _codeInfoRepositoryFactory(worldState), logManager, parallel);
        return (processor, _txProcessorAdapterFactory(processor), virtualMachine);
    }
}
