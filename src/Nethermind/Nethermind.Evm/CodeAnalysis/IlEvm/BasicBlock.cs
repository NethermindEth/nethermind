// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

[Flags]
public enum BasicBlockFlags
{
    None = 0,

    /// <summary>
    /// Every opcode in the block belongs to the IL-EVM v1 subset, so the block can be fed to
    /// the segment compiler. Stack and gas metrics are only computed for compilable blocks.
    /// </summary>
    Compilable = 1 << 0,

    /// <summary>
    /// The block contains opcodes whose gas is only partially static (memory expansion);
    /// the dynamic part must still be charged at the opcode, as the interpreter does.
    /// </summary>
    HasDynamicGas = 1 << 1,

    EndsWithJump = 1 << 2,
    EndsWithConditionalJump = 1 << 3,
    EndsWithTerminator = 1 << 4,
    StartsWithJumpDest = 1 << 5,
}

/// <summary>
/// One unit of the analyzed-code IR: a run of consecutive opcodes with no control-flow entry
/// except at <see cref="Start"/> and no exit except at the end. Blocks are additionally cut at
/// compilable/non-compilable classification changes so that compilable blocks are homogeneous
/// segment-compiler input.
/// </summary>
public readonly struct BasicBlock(int start, int end, long staticGas, int stackRequired, int stackMaxGrowth, int stackDelta, BasicBlockFlags flags)
{
    /// <summary>Program counter of the first opcode in the block.</summary>
    public int Start { get; } = start;

    /// <summary>Exclusive end: the program counter just past the last opcode and its immediates.</summary>
    public int End { get; } = end;

    /// <summary>Sum of the static gas of all opcodes in the block. Valid only for compilable blocks.</summary>
    public long StaticGas { get; } = staticGas;

    /// <summary>Minimum stack depth required on entry. Valid only for compilable blocks.</summary>
    public int StackRequired { get; } = stackRequired;

    /// <summary>Maximum stack growth over the entry depth reached inside the block. Valid only for compilable blocks.</summary>
    public int StackMaxGrowth { get; } = stackMaxGrowth;

    /// <summary>Net stack depth change of the block. Valid only for compilable blocks.</summary>
    public int StackDelta { get; } = stackDelta;

    public BasicBlockFlags Flags { get; } = flags;

    public bool IsCompilable => (Flags & BasicBlockFlags.Compilable) != 0;
}
