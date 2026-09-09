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

        /// <summary>
        /// Returns <c>true</c> when entering this frame credits the executing account balance with the frame value.
        /// </summary>
        /// <remarks>
        /// <see cref="ExecutionType.DELEGATECALL"/> carries the caller's value without moving ETH, while
        /// <see cref="ExecutionType.STATICCALL"/> cannot transfer ETH. <see cref="ExecutionType.CALLCODE"/>
        /// still credits the executing account, which is a self-transfer when caller and target are the same account.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CreditsBalance(this ExecutionType executionType) =>
            executionType is ExecutionType.TRANSACTION or ExecutionType.CALL or ExecutionType.CALLCODE or ExecutionType.CREATE or ExecutionType.CREATE2;

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
