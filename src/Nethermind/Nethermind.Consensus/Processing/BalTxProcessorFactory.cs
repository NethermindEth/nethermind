// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Builds the per-world-state tx processor and its adapter for the EIP-7928 block-access-list pool.
/// </summary>
/// <remarks>
/// Bundles everything <see cref="BlockAccessListManager"/>'s pool needs to construct a processor so the
/// manager takes one collaborator instead of five. <paramref name="adapterFactory"/> and
/// <paramref name="processorFactory"/> default to the standard Execute adapter / Ethereum gas policy for
/// manual construction (stateless envs, tests); container resolution fills them from the scope so the
/// scoped <see cref="ITransactionProcessorAdapter"/> and the pool's per-worker adapters stay on one axis.
/// </remarks>
public class BalTxProcessorFactory(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    ILogManager logManager,
    CodeInfoRepositoryFactory codeInfoRepositoryFactory,
    TransactionProcessorAdapterFactory? adapterFactory = null,
    ITransactionProcessorFactory? processorFactory = null)
{
    private readonly TransactionProcessorAdapterFactory _adapterFactory = adapterFactory ?? CreateExecuteAdapter;
    private readonly ITransactionProcessorFactory _processorFactory = processorFactory ?? new TransactionProcessorFactory<EthereumGasPolicy>();

    public (ITransactionProcessor Processor, ITransactionProcessorAdapter Adapter) Create(IWorldState worldState, bool parallel)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(worldState);
        ITransactionProcessor processor = _processorFactory.Create(
            BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);
        return (processor, _adapterFactory(processor));
    }

    private static ITransactionProcessorAdapter CreateExecuteAdapter(ITransactionProcessor transactionProcessor)
        => new ExecuteTransactionProcessorAdapter(transactionProcessor);
}
