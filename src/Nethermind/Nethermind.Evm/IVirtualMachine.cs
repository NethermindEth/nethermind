// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm
{
    public interface IVirtualMachine
    {
        TransactionSubstate ExecuteTransaction<TTracingInst>(EvmState state, IWorldState worldState, ITxTracer txTracer)
            where TTracingInst : struct, IFlag;
    }
}
