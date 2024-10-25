// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class EncodingExtensions
{
    public static bool TryGetStringSlice(this Encoding encoding, ReadOnlySequence<byte> sequence, int charCount,
        out bool completed, [NotNullWhen(true)] out string? result)
    {
        char[] chars = ArrayPool<char>.Shared.Rent(charCount);

        try
        {
            ReadOnlySpan<byte> first = sequence.FirstSpan;
            encoding.GetDecoder().Convert(first, chars, true, out _, out int charsUsed, out completed);
            result = new(chars.AsSpan(0, charsUsed));
            return true;
        }
        catch (Exception)
        {
            result = null;
            completed = false;
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    public static bool TryGetStringSlice(this Encoding encoding, ReadOnlySequence<byte> sequence, int charCount, [NotNullWhen(true)] out string? result) =>
        TryGetStringSlice(encoding, sequence, charCount, out _, out result);
}
