// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    /// <summary>
    /// Gas-free body of <see cref="InstructionPush{TGasPolicy, TOpCount, TTracingInst}"/>;
    /// run by the stream executor inside precharged basic blocks.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType PushCore<TOpCount, TTracingInst>(ref EvmStack stack, ref int programCounter)
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        EvmExceptionType result = TOpCount.Push<TTracingInst>(TOpCount.Count, ref stack, programCounter);
        programCounter += TOpCount.Count;
        return result;
    }
}
