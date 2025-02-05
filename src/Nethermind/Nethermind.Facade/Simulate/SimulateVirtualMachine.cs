// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Facade.Simulate;

public class SimulateVirtualMachine(IVirtualMachine virtualMachine) : IVirtualMachine
{
    public TransactionSubstate ExecuteTransaction<TTracingInstructions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
            where TTracingInstructions : struct, IFlag
    {
        if (txTracer.IsTracingActions && TryGetLogsMutator(txTracer, out ITxLogsMutator logsMutator))
        {
            logsMutator.SetLogsToMutate(state.AccessTracker.Logs);
        }

        return virtualMachine.ExecuteTransaction<TTracingInstructions>(state, worldState, txTracer);
    }

    private static bool TryGetLogsMutator(ITxTracer txTracer, [NotNullWhen(true)] out ITxLogsMutator? txLogsMutator)
    {
        switch (txTracer)
        {
            case ITxLogsMutator { IsMutatingLogs: true } logsMutator:
                txLogsMutator = logsMutator;
                return true;
            case ITxTracerWrapper txTracerWrapper:
                return TryGetLogsMutator(txTracerWrapper.InnerTracer, out txLogsMutator);
            default:
                txLogsMutator = null;
                return false;
        }
    }
}
