// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Builds per-worker <see cref="ITransactionProcessor"/> instances bound to a caller-supplied
/// <see cref="IWorldState"/> / <see cref="IVirtualMachine"/> / <see cref="ICodeInfoRepository"/>.
/// Non-generic so callers (e.g. <c>BlockAccessListManager</c>) don't have to spell out a gas
/// policy; the generic policy lives on the implementation.
/// </summary>
public interface ITransactionProcessorFactory
{
    ITransactionProcessor Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager);
}

/// <summary>Default factory producing a plain <see cref="TransactionProcessor{TGasPolicy}"/>.</summary>
public class TransactionProcessorFactory<TGasPolicy> : ITransactionProcessorFactory
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public virtual ITransactionProcessor Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager)
        => new TransactionProcessor<TGasPolicy>(
            blobBaseFeeCalculator, specProvider, worldState, (IVirtualMachine<TGasPolicy>)virtualMachine,
            codeInfoRepository, logManager);
}
