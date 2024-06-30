// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm
{
    public static class ExecutionTypeExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCreateLegacy(this ExecutionType executionType) =>
            executionType is ExecutionType.CREATE or ExecutionType.CREATE2;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCreateEof(this ExecutionType executionType) =>
            executionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE;
        // did not want to use flags here specifically
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCreate(this ExecutionType executionType) =>
            IsAnyCreateLegacy(executionType) || IsAnyCreateEof(executionType);

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
                ExecutionType.EOFCREATE => Instruction.EOFCREATE,
                ExecutionType.EOFCALL => Instruction.EXTCALL,
                ExecutionType.EOFSTATICCALL => Instruction.EXTSTATICCALL,
                ExecutionType.EOFDELEGATECALL => Instruction.EXTDELEGATECALL,
                _ => throw new NotSupportedException($"Execution type {executionType} is not supported.")
            };
    }

    // ReSharper disable InconsistentNaming IdentifierTypo
    public enum ExecutionType
    {
        TRANSACTION,
        CALL,
        STATICCALL,
        DELEGATECALL,
        CALLCODE,
        CREATE,
        CREATE2,
        EOFCREATE,
        TXCREATE,
        EOFCALL,
        EOFSTATICCALL,
        EOFDELEGATECALL,
    }
    // ReSharper restore IdentifierTypo InconsistentNaming
}
