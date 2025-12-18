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
        GasUInt64Overflow,
        InvalidSubroutineEntry,
        InvalidSubroutineReturn,
        InvalidJumpDestination,
        AccessViolation,
        AddressOutOfRange,
        StaticCallViolation,
        PrecompileFailure,
        TransactionCollision,
        NotEnoughBalance,
        Other,
        Revert,
        InvalidCode,
    }
}
