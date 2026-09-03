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
    /// <summary>Not a failure: the frame yielded a child call/create frame and is suspended until it returns.</summary>
    Suspend,
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
        EvmExceptionType.Suspend => nameof(EvmExceptionType.Suspend),
        _ => ((int)type).ToString(),
    };
}

/// <summary>A jump handler's outcome: the program counter it selected, packed with its halting status.</summary>
/// <remarks>
/// The counter occupies the low 32 bits and the <see cref="EvmExceptionType"/> the high 32. Handlers that
/// move the counter would otherwise take it by reference, which forces it into an address-taken stack slot
/// for as long as the body runs; the jump handlers pay that because they call a helper that does not inline,
/// and <c>JUMPI</c> does not inline into the dispatch loop at all. Returning it keeps it in a register.
/// One <see cref="ulong"/> wide because a 16-byte struct returns through memory on the Windows x64 ABI.
/// </remarks>
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct OpcodeResult(nint programCounter, EvmExceptionType exception)
{
    private readonly ulong _packed = ((ulong)(uint)exception << 32) | (uint)programCounter;

    /// <summary>The counter the handler selected, whether or not it halted.</summary>
    public nint ProgramCounter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (nint)(uint)_packed;
    }

    /// <summary>Why the handler halted, or <see cref="EvmExceptionType.None"/> if it did not.</summary>
    public EvmExceptionType Exception
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (EvmExceptionType)(uint)(_packed >> 32);
    }
}
