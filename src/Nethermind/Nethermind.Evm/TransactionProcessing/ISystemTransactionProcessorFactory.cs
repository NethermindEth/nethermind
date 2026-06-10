// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Per-worker factory for <see cref="SystemTransactionProcessor{TGasPolicy}"/>. Consensus
/// engines that need a custom system-transaction processor (e.g. AuRa) register an
/// implementation via DI; the default returns the plain processor.
/// </summary>
/// <remarks>
/// Each <see cref="TransactionProcessorBase{TGasPolicy}"/> instance (including BAL parallel-pool
/// workers, which hand-construct their own <see cref="TransactionProcessor{TGasPolicy}"/> with
/// the worker's BAL-traced <see cref="IWorldState"/>) calls <see cref="Create"/> with its own
/// <paramref name="worldState"/> and <paramref name="virtualMachine"/>, so the factory itself
/// is stateless and singleton-safe.
/// </remarks>
public interface ISystemTransactionProcessorFactory<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    SystemTransactionProcessor<TGasPolicy> Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine<TGasPolicy> virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager);
}

/// <summary>Default factory returning a plain <see cref="SystemTransactionProcessor{TGasPolicy}"/>.</summary>
public sealed class DefaultSystemTransactionProcessorFactory<TGasPolicy>
    : ISystemTransactionProcessorFactory<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public static readonly DefaultSystemTransactionProcessorFactory<TGasPolicy> Instance = new();

    public SystemTransactionProcessor<TGasPolicy> Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine<TGasPolicy> virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager)
        => new(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
}
