// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Exceptions;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Validates EIP-1474 QUANTITY hex encoding rules (no leading zeros, except "0x0").
/// </summary>
internal static class QuantityValidator
{
    /// <summary>
    /// Throws if <paramref name="span"/> is a hex QUANTITY with leading zero digits.
    /// </summary>
    /// <remarks>
    /// A leading zero is defined as a hex body longer than one digit where the first digit is '0'.
    /// "0x0" is the canonical zero and is always valid.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AssertNoLeadingZero(ReadOnlySpan<byte> span)
    {
        if (span.StartsWith("0x"u8) && span.Length > 3 && span[2] == (byte)'0')
            ThrowLeadingZero();
    }

    [DoesNotReturn, StackTraceHidden]
    internal static void ThrowLeadingZero() =>
        throw new SafePublicMessageFormatException("hex number with leading zero digits");
}
