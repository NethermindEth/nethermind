// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Evm;

namespace Nethermind.Core.Test.Builders;

/// <summary>
/// Bytecode whose struct logs are large: every entry carries a deep stack and, optionally, growing memory.
/// </summary>
public static class DeepStackLoopCode
{
    /// <summary>
    /// PUSH32 a non-zero word, DUP1 it to <paramref name="stackDepth"/>, then loop: counter += stride,
    /// MSTORE8 the counter at offset counter, jump back. The stack depth stays constant and memory grows by
    /// <paramref name="memoryStride"/> bytes per iteration until the transaction runs out of gas.
    /// </summary>
    public static byte[] Build(int stackDepth, int memoryStride)
    {
        int loopStart = 33 + (stackDepth - 1) + 2;
        return
        [
            (byte)Instruction.PUSH32,
            .. Enumerable.Repeat((byte)0xff, 32),
            .. Enumerable.Repeat((byte)Instruction.DUP1, stackDepth - 1),
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH2, (byte)(memoryStride >> 8), (byte)memoryStride,
            (byte)Instruction.ADD,
            (byte)Instruction.DUP1,
            (byte)Instruction.DUP1,
            (byte)Instruction.MSTORE8,
            (byte)Instruction.PUSH2, (byte)(loopStart >> 8), (byte)loopStart,
            (byte)Instruction.JUMP,
        ];
    }
}
