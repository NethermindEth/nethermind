// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// <see cref="ITransactionProcessorFactory"/>, scope-specific <see cref="CodeInfoRepositoryFactory"/> and
/// <see cref="TransactionProcessorAdapterFactory"/>) automatically. The optional parameters default to the
/// standard Ethereum implementations only at manual construction sites (stateless envs, tests) — in
/// container scopes the registered services are always injected.
/// </remarks>
public class BalTxProcessorFactory(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    ILogManager logManager,
    CodeInfoRepositoryFactory? codeInfoRepositoryFactory = null,
    TransactionProcessorAdapterFactory? txProcessorAdapterFactory = null,
    ITransactionProcessorFactory? transactionProcessorFactory = null)
{
    private static readonly CodeInfoRepositoryFactory DefaultCodeInfoRepositoryFactory =
        static worldState => new EthereumCodeInfoRepository(worldState);
    private static readonly TransactionProcessorAdapterFactory DefaultTxProcessorAdapterFactory =
        static txProcessor => new ExecuteTransactionProcessorAdapter(txProcessor);

    private readonly CodeInfoRepositoryFactory _codeInfoRepositoryFactory = codeInfoRepositoryFactory ?? DefaultCodeInfoRepositoryFactory;
    private readonly TransactionProcessorAdapterFactory _txProcessorAdapterFactory = txProcessorAdapterFactory ?? DefaultTxProcessorAdapterFactory;
    private readonly ITransactionProcessorFactory _transactionProcessorFactory = transactionProcessorFactory ?? new TransactionProcessorFactory<EthereumGasPolicy>();

    public (ITransactionProcessor Processor, ITransactionProcessorAdapter Adapter) Create(IWorldState worldState, bool parallel)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        ITransactionProcessor processor = _transactionProcessorFactory.Create(
            BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine,
            _codeInfoRepositoryFactory(worldState), logManager, parallel);
        return (processor, _txProcessorAdapterFactory(processor));
    }
}
