// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Represents a chunk of <see cref="Instruction"/>s that is optimized and ready to be run in an efficient manner.
/// </summary>
///
internal interface InstructionChunk
{
    string Name { get; }
    void Invoke<T>(EvmState vmState,
            ulong chainId,
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
            ITxTracer trace,
            ILogger logger,
            ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing;
}
