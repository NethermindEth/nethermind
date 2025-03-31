// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class EncodingExtensions
{
    private static string GetStringSlice(Encoding encoding, ReadOnlySpan<byte> span, Span<char> chars, out bool completed)
    {
        encoding.GetDecoder().Convert(span, chars, true, out _, out int charsUsed, out completed);
        return new(chars[..charsUsed]);
    }

    private static string GetStringSliceMultiSegment(Encoding encoding, ref readonly ReadOnlySequence<byte> sequence, Span<char> chars, out bool completed)
    {
        try
        {
            var charsUsed = encoding.GetChars(sequence, chars);
            completed = true;
            return new(chars[..charsUsed]);
        }
        // Thrown when decoder detects that chars array is not enough to contain the result
        // If this happens, whole array should already be filled
        catch (ArgumentException exception) when (exception.ParamName == "chars")
        {
            completed = false;
            return new(chars);
        }
    }

    /// <summary>
    /// Attempts to decode up to <paramref name="charCount"/> characters from byte <paramref name="sequence"/> using provided <paramref name="encoding"/>.
    /// </summary>
    /// <param name="charCount">Maximum number of characters to decode.</param>
    /// <param name="encoding">Encoding to use.</param>
    /// <param name="sequence">Bytes sequence.</param>
    /// <param name="completed"><c>true</c> if the whole <paramref name="sequence"/> was decoded, <c>false</c> otherwise.</param>
    /// <param name="result">Decoded string of up to <see cref="charCount"/> characters.</param>
    /// <returns>
    /// <c>true</c>, if successfully decoded whole string or the specified <paramref name="charCount"/>, <c>false</c> in case of an error.
    /// </returns>
    public static bool TryGetStringSlice(this Encoding encoding, in ReadOnlySequence<byte> sequence, int charCount,
        out bool completed, [NotNullWhen(true)] out string? result)
    {
        char[] charArray = ArrayPool<char>.Shared.Rent(charCount);
        Span<char> chars = charArray.AsSpan(0, charCount);

        try
        {
            result = sequence.IsSingleSegment
                ? GetStringSlice(encoding, sequence.FirstSpan, chars, out completed)
                : GetStringSliceMultiSegment(encoding, in sequence, chars, out completed);

            return true;
        }
        // Failed to parse, should only happen if bytes encoding is invalid
        catch (Exception)
        {
            result = null;
            completed = false;
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    /// <inheritdoc cref="TryGetStringSlice(System.Text.Encoding,in System.Buffers.ReadOnlySequence{byte},int,out bool,out string?)"/>
    public static bool TryGetStringSlice(this Encoding encoding, in ReadOnlySequence<byte> sequence, int charCount, [NotNullWhen(true)] out string? result) =>
        TryGetStringSlice(encoding, in sequence, charCount, out _, out result);
}
