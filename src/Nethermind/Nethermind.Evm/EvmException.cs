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
        None,
        BadInstruction,
        StackOverflow,
        StackUnderflow,
        OutOfGas,
        GasUInt64Overflow,
        InvalidSubroutineEntry,
        InvalidSubroutineReturn,
        InvalidJumpDestination,
        AccessViolation,
        StaticCallViolation,
        PrecompileFailure,
        TransactionCollision,
        NotEnoughBalance,
        Other,
        Revert,
        InvalidCode
    }
}
