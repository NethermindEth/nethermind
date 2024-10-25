// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class EncodingExtensions
{
    private class LimitedArrayBufferWriter<T>(T[] buffer, int limit) : IBufferWriter<T>
    {
        private int _index;

        public void Advance(int count)
        {
            if (count < 0)
                throw new ArgumentException(null, nameof(count));

            _index += count;
        }

        public Memory<T> GetMemory(int sizeHint = 0) => buffer.AsMemory(_index, limit - _index);

        public Span<T> GetSpan(int sizeHint = 0) => buffer.AsSpan(_index, limit - _index);
    }

    private static string GetStringSlice(this Encoding encoding, ReadOnlySpan<byte> span, Span<char> chars, out bool completed)
    {
        encoding.GetDecoder().Convert(span, chars, true, out _, out int charsUsed, out completed);
        return new(chars[..charsUsed]);
    }

    private static string GetStringSliceMultiSegment(this Encoding encoding, ReadOnlySequence<byte> sequence, char[] charArray, int charCount,
        out bool completed)
    {
        try
        {
            var writer = new LimitedArrayBufferWriter<char>(charArray, charCount);
            encoding.GetDecoder().Convert(sequence, writer, true, out long charsUsed, out completed);
            return new(charArray.AsSpan(0, (int)charsUsed));
        }
        catch (ArgumentException exception) when (exception.ParamName == "chars")
        {
            completed = false;
            return  new(charArray.AsSpan(0, charCount));
        }
    }

    public static bool TryGetStringSlice(this Encoding encoding, ReadOnlySequence<byte> sequence, int charCount,
        out bool completed, [NotNullWhen(true)] out string? result)
    {
        char[] charArray = ArrayPool<char>.Shared.Rent(charCount);

        try
        {
            result = sequence.IsSingleSegment
                ? GetStringSlice(encoding, sequence.FirstSpan, charArray.AsSpan(0, charCount), out completed)
                : GetStringSliceMultiSegment(encoding, sequence, charArray, charCount, out completed);

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
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    public static bool TryGetStringSlice(this Encoding encoding, ReadOnlySequence<byte> sequence, int charCount, [NotNullWhen(true)] out string? result) =>
        TryGetStringSlice(encoding, sequence, charCount, out _, out result);
}
