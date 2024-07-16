// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        if (default(T) is not null && value is null)
        {
            ThrowArgumentNullException(argName);
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowArgumentNullException(string argName)
    {
        throw new ArgumentNullException(argName);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowNotSupportedException()
    {
        throw new NotSupportedException();
    }

    [DoesNotReturn]
    [StackTraceHidden]
    public static void ThrowInvalidOperationException_NoWritingAllowed()
    {
        throw new InvalidOperationException("No Writing Allowed");
    }

    [DoesNotReturn]
    [StackTraceHidden]
    public static void ThrowArgumentOutOfRangeException_SizeHint()
    {
        throw new ArgumentOutOfRangeException("sizeHint");
    }

    [DoesNotReturn]
    [StackTraceHidden]
    public static void ThrowArgumentNullException_WritingStream()
    {
        throw new ArgumentNullException("writingStream");
    }

    [DoesNotReturn]
    [StackTraceHidden]
    public static void ThrowArgumentOutOfRangeException_Bytes()
    {
        throw new ArgumentOutOfRangeException("bytes");
    }
}
