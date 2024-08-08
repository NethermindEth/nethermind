// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm
{
    public interface IVirtualMachine
    {
        TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
            where TTracingActions : struct, IIsTracing;
    }

    internal interface IEvm : IVirtualMachine
    {
        IReleaseSpec Spec { get; }
        EvmState State { get; }
        ITxTracer TxTracer { get; }
        IWorldState WorldState { get; }
        ReadOnlySpan<byte> ChainId { get; }
        ICodeInfoRepository CodeInfoRepository { get; }
        ReadOnlyMemory<byte> ReturnDataBuffer { get; set; }
        IBlockhashProvider BlockhashProvider { get; }
        int SectionIndex { get; set; }
        object ReturnData { get; set; }
    }
}
