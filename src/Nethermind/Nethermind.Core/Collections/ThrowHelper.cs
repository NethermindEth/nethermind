// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

public class ThrowHelper
{
    // Allow nulls for reference types and Nullable<U>, but not for value types.
    // Aggressively inline so the jit evaluates the if in place and either drops the call altogether
    // Or just leaves null test and call to the Non-returning ThrowHelper.ThrowArgumentNullException
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IfNullAndNullsAreIllegalThenThrow<T>(object? value, string argName)
    {
        // Note that default(T) is not equal to null for value types except when T is Nullable<U>.
        if (default(T) != null && value == null)
            throw new ArgumentNullException(argName);
    }
}
