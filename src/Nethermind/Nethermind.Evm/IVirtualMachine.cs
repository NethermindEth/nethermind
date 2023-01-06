// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm
{
    public interface IVirtualMachine
    {
        TransactionSubstate Run(EvmState state, IWorldState worldState, ITxTracer tracer);

        CodeInfo GetCachedCodeInfo(IWorldState state, Address codeSource, IReleaseSpec spec);
    }
}
