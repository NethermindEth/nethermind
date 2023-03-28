// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm
{
    public static class ExecutionTypeExtensions
    {
        // did not want to use flags here specifically
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCreate(this ExecutionType executionType)
        {
            return executionType == ExecutionType.Create || executionType == ExecutionType.Create2
                || executionType == ExecutionType.Create3 || executionType == ExecutionType.Create4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyCreateEof(this ExecutionType executionType)
        {
            return executionType == ExecutionType.Create3 || executionType == ExecutionType.Create4;
        }
    }

    public enum ExecutionType
    {
        Transaction,
        Call,
        StaticCall,
        CallCode,
        DelegateCall,
        Create,
        Create2,
        Create3,
        Create4,
    }
}
