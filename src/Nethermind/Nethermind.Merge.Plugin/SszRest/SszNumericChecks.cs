// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Nethermind.Merge.Plugin.SszRest;

internal static class SszNumericChecks
{
    /// <summary>
    /// Converts an SSZ <c>uint64</c> value to <see cref="long"/>, throwing
    /// <see cref="InvalidDataException"/> when the value exceeds <see cref="long.MaxValue"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long CheckedLong(ulong value)
    {
        if (value > (ulong)long.MaxValue)
            ThrowLongOutOfRange(value);

        return (long)value;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowLongOutOfRange(ulong value) =>
            throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for long");
    }
}
