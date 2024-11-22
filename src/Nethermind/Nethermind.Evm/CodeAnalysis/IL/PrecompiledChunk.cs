// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using System;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class PrecompiledChunk : InstructionChunk
{
    public string Name => PrecompiledSegment.Method.Name;
    internal ExecuteSegment PrecompiledSegment;
    internal byte[][] Data;
    internal int[] JumpDestinations;

    public void Invoke<T>(EvmState vmState,
        ulong chainId,
        ref ReadOnlyMemory<byte> outputBuffer,
        IBlockhashProvider blockhashProvider,
        IWorldState worldState,
        ICodeInfoRepository codeInfoRepository,
        IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ITxTracer trace,
        ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
    {
        vmState.DataStackHead = stack.Head;
        var ilvmState = new ILEvmState(chainId, vmState, EvmExceptionType.None, ref outputBuffer);
        PrecompiledSegment(ref ilvmState, blockhashProvider, worldState, codeInfoRepository, spec, trace, ref programCounter, ref gasAvailable, Data);

        result = (ILChunkExecutionResult)ilvmState;
        stack.Head = ilvmState.StackHead;
    }
}
