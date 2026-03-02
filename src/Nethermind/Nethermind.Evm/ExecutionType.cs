// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public static class ExecutionTypeExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAnyCreate(this ExecutionType executionType) =>
        (executionType & ExecutionType.IsCreate) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAnyDelegateCall(this ExecutionType executionType) =>
        executionType is ExecutionType.DELEGATECALL;

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
[Flags]
public enum ExecutionType : byte
{
    TRANSACTION = 0,
    CALL = 1 | IsCall,
    STATICCALL = 2 | IsCall,
    DELEGATECALL = 3 | IsCall,
    CALLCODE = 4 | IsCall,
    CREATE = 5 | IsCreate,
    CREATE2 = 6 | IsCreate,

    IsCreate = 16,
    IsCall = 32,
}
// ReSharper restore IdentifierTypo InconsistentNaming
