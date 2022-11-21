// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            return executionType == ExecutionType.Create || executionType == ExecutionType.Create2;
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
        Create2
    }
}
