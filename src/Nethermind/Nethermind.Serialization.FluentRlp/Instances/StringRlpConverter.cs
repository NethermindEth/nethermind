// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text;

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts a <see cref="string"/> as a byte string of its UTF-8 bytes.</summary>
public abstract class StringRlpConverter : IRlpConverter<string>
{
    private const int MaxStackSize = 256;

    // Throws on invalid UTF-8, so that two different encodings never decode to the same string.
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <inheritdoc/>
    /// <exception cref="RlpReaderException">The item is not a byte string of valid UTF-8.</exception>
    public static string Read(ref RlpReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.ReadBytes();
        try
        {
            return Encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new RlpReaderException("RLP string is not valid UTF-8");
        }
    }

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, string value)
    {
        int maxByteCount = Encoding.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> buffer = maxByteCount <= MaxStackSize
            ? stackalloc byte[MaxStackSize]
            : rented = ArrayPool<byte>.Shared.Rent(maxByteCount);

        int byteCount = Encoding.GetBytes(value, buffer);
        writer.Write(buffer[..byteCount]);

        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

/// <summary>Extension methods for <see cref="StringRlpConverter"/>.</summary>
public static class StringRlpConverterExt
{
    /// <inheritdoc cref="StringRlpConverter.Read"/>
    public static string ReadString(this ref RlpReader reader) => StringRlpConverter.Read(ref reader);

    /// <inheritdoc cref="StringRlpConverter.Write"/>
    public static void Write(this ref RlpWriter writer, string value) => StringRlpConverter.Write(ref writer, value);
}
