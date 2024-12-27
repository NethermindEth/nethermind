// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL.CompilerModes.PartialAOT;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System;
using System.Runtime.CompilerServices;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class PrecompiledChunk : InstructionChunk
{
    public string Name => PrecompiledSegment.Method.Name;
    internal PartialAOT.ExecuteSegment PrecompiledSegment;
    internal byte[][] Data;

    public void Invoke<T>(EvmState vmState,
        ulong chainId,
        ref ReadOnlyMemory<byte> outputBuffer,
        in ExecutionEnvironment env,
        in TxExecutionContext txCtx,
        in BlockExecutionContext blkCtx,
        IBlockhashProvider blockhashProvider,
        IWorldState worldState,
        ICodeInfoRepository codeInfoRepository,
        IReleaseSpec spec,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack,
        ref ReadOnlyMemory<byte> returnDataBuffer,
        ITxTracer tracer,
        ILogger logger,
        ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
    {
        PrecompiledSegment.Invoke(
            chainId,
            ref vmState,
            in env,
            in txCtx,
            in blkCtx,
            ref vmState.Memory,

            ref Unsafe.As<byte, Word>(ref stack.HeadRef),
            ref stack.Head,

            blockhashProvider,
            worldState,
            codeInfoRepository,
            spec,
            tracer,
            logger,

            ref programCounter,
            ref gasAvailable,

            env.CodeInfo.MachineCode,
            in env.InputData,
            ref returnDataBuffer,
            Data,
            ref result);
    }
}
