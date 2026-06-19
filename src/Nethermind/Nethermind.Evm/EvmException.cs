// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm
{
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
        /// Reflection-free equivalent of <see cref="object.ToString"/> for <see cref="EvmExceptionType"/>.
        /// The trimmed NativeAOT/zkVM runtime has no enum metadata, so <c>Enum.ToString()</c> faults in
        /// <c>ReflectionAugments.GetEnumInfo</c>. A top-level transaction can legitimately fail
        /// (Revert/OutOfGas/...) and the receipts tracer formats the error name, so map it directly.
        /// </summary>
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
}
