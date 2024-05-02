// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public static class EvmExceptionExtensions
{
    public static string? GetEvmExceptionDescription(this EvmExceptionType evmExceptionType) =>
        evmExceptionType switch
        {
            EvmExceptionType.None => null,
            EvmExceptionType.BadInstruction => "invalid instruction",
            EvmExceptionType.StackOverflow => "max call depth exceeded",
            EvmExceptionType.StackUnderflow => "stack underflow",
            EvmExceptionType.OutOfGas => "out of gas",
            EvmExceptionType.GasUInt64Overflow => "gas uint64 overflow",
            EvmExceptionType.InvalidSubroutineEntry => "invalid jump destination",
            EvmExceptionType.InvalidSubroutineReturn => "invalid jump destination",
            EvmExceptionType.InvalidJumpDestination => "invalid jump destination",
            EvmExceptionType.AccessViolation => "return data out of bounds",
            EvmExceptionType.StaticCallViolation => "write protection",
            EvmExceptionType.PrecompileFailure => "precompile error",
            EvmExceptionType.TransactionCollision => "contract address collision",
            EvmExceptionType.NotEnoughBalance => "insufficient balance for transfer",
            EvmExceptionType.Other => "error",
            EvmExceptionType.Revert => "execution reverted",
            EvmExceptionType.InvalidCode => "invalid code: must not begin with 0xef",
            _ => "error"
        };
}
