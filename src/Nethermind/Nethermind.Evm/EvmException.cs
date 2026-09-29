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
        _ => ((int)type).ToString(),
    };
}
