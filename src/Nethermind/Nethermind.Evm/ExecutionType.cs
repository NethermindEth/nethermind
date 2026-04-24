// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class ExecutionTypeExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCreate(this ExecutionType executionType) =>
            executionType is ExecutionType.CREATE or ExecutionType.CREATE2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCall(this ExecutionType executionType) =>
            executionType is ExecutionType.CALL or ExecutionType.STATICCALL or ExecutionType.DELEGATECALL or ExecutionType.CALLCODE;

        // STATICCALL forbids any state change; DELEGATECALL reuses the caller's context with no transfer.
        // Every other ExecutionType (TRANSACTION, CALL, CALLCODE, CREATE, CREATE2) moves ETH to ExecutingAccount on entry.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CreditsBalance(this ExecutionType executionType) =>
            executionType is not (ExecutionType.STATICCALL or ExecutionType.DELEGATECALL);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly UInt256 GetBalanceCredit(this ExecutionType executionType, in UInt256 value) =>
            ref executionType.CreditsBalance() ? ref value : ref UInt256.Zero;

        public static Instruction ToInstruction(this ExecutionType executionType) =>
            executionType switch
            {
                ExecutionType.TRANSACTION => Instruction.CALL,
                ExecutionType.CALL => Instruction.CALL,
                ExecutionType.STATICCALL => Instruction.STATICCALL,
                ExecutionType.CALLCODE => Instruction.CALLCODE,
                ExecutionType.DELEGATECALL => Instruction.DELEGATECALL,
                ExecutionType.CREATE => Instruction.CREATE,
                ExecutionType.CREATE2 => Instruction.CREATE2,
                _ => throw new NotSupportedException($"Execution type {executionType} is not supported.")
            };
    }

    // ReSharper disable InconsistentNaming IdentifierTypo
    public enum ExecutionType : byte
    {
        TRANSACTION,
        CALL,
        STATICCALL,
        DELEGATECALL,
        CALLCODE,
        CREATE,
        CREATE2,
    }
    // ReSharper restore IdentifierTypo InconsistentNaming
}
