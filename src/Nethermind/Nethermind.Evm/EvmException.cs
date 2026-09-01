// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public abstract class EvmException : Exception
{
    public abstract EvmExceptionType ExceptionType { get; }
}

public enum EvmExceptionType
{
    Stop = -1,
    None = 0,
    BadInstruction,
    StackOverflow,
    StackUnderflow,
    OutOfGas,
    InvalidJumpDestination,
    AccessViolation,
    StaticCallViolation,
    PrecompileFailure,
    TransactionCollision,
    NotEnoughBalance,
    Other,
    Revert,
    InvalidCode,
}

public static class EvmExceptionTypeExtensions
{
    /// <summary>
    /// Returns the member name of <paramref name="type"/>, equivalent to <see cref="object.ToString"/> but reflection-free.
    /// </summary>
    /// <remarks>
    /// The trimmed NativeAOT/zkVM runtime carries no enum metadata, so <c>Enum.ToString()</c> faults in
    /// <c>ReflectionAugments.GetEnumInfo</c>. A top-level transaction can legitimately fail
    /// (Revert/OutOfGas/...) and the receipts tracer formats the error name, so the names are mapped directly.
    /// </remarks>
    public static string FastToString(this EvmExceptionType type) => type switch
    {
        EvmExceptionType.Stop => nameof(EvmExceptionType.Stop),
        EvmExceptionType.None => nameof(EvmExceptionType.None),
        EvmExceptionType.BadInstruction => nameof(EvmExceptionType.BadInstruction),
        EvmExceptionType.StackOverflow => nameof(EvmExceptionType.StackOverflow),
        EvmExceptionType.StackUnderflow => nameof(EvmExceptionType.StackUnderflow),
        EvmExceptionType.OutOfGas => nameof(EvmExceptionType.OutOfGas),
        EvmExceptionType.InvalidJumpDestination => nameof(EvmExceptionType.InvalidJumpDestination),
        EvmExceptionType.AccessViolation => nameof(EvmExceptionType.AccessViolation),
        EvmExceptionType.StaticCallViolation => nameof(EvmExceptionType.StaticCallViolation),
        EvmExceptionType.PrecompileFailure => nameof(EvmExceptionType.PrecompileFailure),
        EvmExceptionType.TransactionCollision => nameof(EvmExceptionType.TransactionCollision),
        EvmExceptionType.NotEnoughBalance => nameof(EvmExceptionType.NotEnoughBalance),
        EvmExceptionType.Other => nameof(EvmExceptionType.Other),
        EvmExceptionType.Revert => nameof(EvmExceptionType.Revert),
        EvmExceptionType.InvalidCode => nameof(EvmExceptionType.InvalidCode),
        _ => ((int)type).ToString(),
    };
}

/// <summary>The result of one opcode handler: the next program counter packed with the exception classification.</summary>
/// <remarks>
/// The program counter occupies the low 32 bits and the <see cref="EvmExceptionType"/> the high 32,
/// so any non-<see cref="EvmExceptionType.None"/> result (including <see cref="EvmExceptionType.Stop"/>,
/// whose negative value sets every high bit) makes <see cref="Value"/> exceed any code length. The
/// dispatch loop's single unsigned bounds compare then doubles as the exception check, and passing the
/// counter by value keeps it in a register instead of an address-taken stack slot.
/// </remarks>
public readonly struct OpcodeResult
{
    public readonly ulong Value;

    public int ProgramCounter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(uint)Value;
    }

    public EvmExceptionType Exception
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (EvmExceptionType)(uint)(Value >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OpcodeResult(int pc, EvmExceptionType ex) => Value = ((ulong)(uint)ex << 32) | (uint)pc;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OpcodeResult(int pc) => Value = (uint)pc;
}
