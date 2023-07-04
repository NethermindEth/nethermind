// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm
{
    public interface IVirtualMachine
    {
        TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
            where TTracingActions : struct, IIsTracing;

        CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec spec);
    }
}
