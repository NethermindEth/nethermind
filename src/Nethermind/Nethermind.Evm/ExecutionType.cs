// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public static class ExecutionTypeExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAnyCreateLegacy(this ExecutionType executionType) =>
        executionType is ExecutionType.CREATE or ExecutionType.CREATE2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAnyCreateEof(this ExecutionType executionType) =>
        executionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAnyCreate(this ExecutionType executionType) =>
        (executionType & ExecutionType.IsCreate) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAnyCallEof(this ExecutionType executionType) =>
        executionType is ExecutionType.EOFCALL or ExecutionType.EOFSTATICCALL or ExecutionType.EOFDELEGATECALL;

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
[Flags]
public enum ExecutionType : byte
{
    TRANSACTION = 0,
    CALL = 1,
    STATICCALL = 2,
    DELEGATECALL = 3,
    CALLCODE = 4,
    CREATE = 5 | IsCreate,
    CREATE2 = 6 | IsCreate,
    EOFCREATE = 7 | IsCreate | IsEof,
    TXCREATE = 8 | IsCreate | IsEof,
    EOFCALL = 9 | IsEof,
    EOFSTATICCALL = 10 | IsEof,
    EOFDELEGATECALL = 11 | IsEof,

    IsCreate = 16,
    IsEof = 32
}
// ReSharper restore IdentifierTypo InconsistentNaming
