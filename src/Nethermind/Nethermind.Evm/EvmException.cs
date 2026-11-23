// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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
    /// <summary>
    /// Not a failure: the frame yielded a child call/create frame and is suspended until it returns.
    /// Never observed outside the dispatch loops, which fold it into the success path before the frame's
    /// result is built.
    /// </summary>
    Suspend,
    /// <summary>
    /// A CALL or CREATE at the maximum call depth, which fails its precheck and enters no frame.
    /// Reported to action tracers only; the operation itself pushes 0 and execution continues.
    /// </summary>
    CallDepthExceeded,
    /// <summary>EIP-7979: <c>CALLSUB</c> with a return stack already holding its limit of addresses.</summary>
    ReturnStackOverflow,
    /// <summary>EIP-7979: <c>RETURNSUB</c> with an empty return stack.</summary>
    ReturnStackUnderflow,
    /// <summary>
    /// A CREATE from an account whose nonce cannot be incremented, which fails its precheck and enters no frame.
    /// Reported to action tracers only; the operation itself pushes 0 and execution continues.
    /// </summary>
    NonceOverflow,
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
        EvmExceptionType.CallDepthExceeded => nameof(EvmExceptionType.CallDepthExceeded),
        EvmExceptionType.ReturnStackOverflow => nameof(EvmExceptionType.ReturnStackOverflow),
        EvmExceptionType.ReturnStackUnderflow => nameof(EvmExceptionType.ReturnStackUnderflow),
        EvmExceptionType.NonceOverflow => nameof(EvmExceptionType.NonceOverflow),
        _ => ((int)type).ToString(),
    };

    /// <summary>
    /// Returns the <see cref="EvmExceptionType"/> whose <see cref="FastToString"/> name is <paramref name="name"/>,
    /// or <see cref="EvmExceptionType.None"/> when no member has that name.
    /// </summary>
    /// <remarks>Reflection-free for the same reason as <see cref="FastToString"/>.</remarks>
    public static EvmExceptionType FastParse(string? name) => name switch
    {
        nameof(EvmExceptionType.Stop) => EvmExceptionType.Stop,
        nameof(EvmExceptionType.BadInstruction) => EvmExceptionType.BadInstruction,
        nameof(EvmExceptionType.StackOverflow) => EvmExceptionType.StackOverflow,
        nameof(EvmExceptionType.StackUnderflow) => EvmExceptionType.StackUnderflow,
        nameof(EvmExceptionType.OutOfGas) => EvmExceptionType.OutOfGas,
        nameof(EvmExceptionType.InvalidJumpDestination) => EvmExceptionType.InvalidJumpDestination,
        nameof(EvmExceptionType.AccessViolation) => EvmExceptionType.AccessViolation,
        nameof(EvmExceptionType.StaticCallViolation) => EvmExceptionType.StaticCallViolation,
        nameof(EvmExceptionType.PrecompileFailure) => EvmExceptionType.PrecompileFailure,
        nameof(EvmExceptionType.TransactionCollision) => EvmExceptionType.TransactionCollision,
        nameof(EvmExceptionType.NotEnoughBalance) => EvmExceptionType.NotEnoughBalance,
        nameof(EvmExceptionType.Other) => EvmExceptionType.Other,
        nameof(EvmExceptionType.Revert) => EvmExceptionType.Revert,
        nameof(EvmExceptionType.InvalidCode) => EvmExceptionType.InvalidCode,
        nameof(EvmExceptionType.Suspend) => EvmExceptionType.Suspend,
        nameof(EvmExceptionType.CallDepthExceeded) => EvmExceptionType.CallDepthExceeded,
        nameof(EvmExceptionType.ReturnStackOverflow) => EvmExceptionType.ReturnStackOverflow,
        nameof(EvmExceptionType.ReturnStackUnderflow) => EvmExceptionType.ReturnStackUnderflow,
        nameof(EvmExceptionType.NonceOverflow) => EvmExceptionType.NonceOverflow,
        _ => EvmExceptionType.None,
    };
}
