// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

// TODO Optimism: made to pass OptimismTransactionProcessor into ReadOnlyTxProcessingEnv. Maybe we can find better solution
public interface ITransactionProcessorFactory
{
    ITransactionProcessor Create(
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ILogManager? logManager);
}

public class TransactionProcessorFactory : ITransactionProcessorFactory
{
    public ITransactionProcessor Create(ISpecProvider? specProvider, IWorldState? worldState,
        IVirtualMachine? virtualMachine, ILogManager? logManager)
    {
        return new TransactionProcessor(specProvider, worldState, virtualMachine, logManager);
    }
}
