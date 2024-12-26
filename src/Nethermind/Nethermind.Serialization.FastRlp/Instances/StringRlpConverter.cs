// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text;

namespace Nethermind.Serialization.FastRlp.Instances;

public abstract class StringRlpConverter : IRlpConverter<string>
{
    private const int MaxStackSize = 256;
    private static readonly Encoding Encoding = Encoding.UTF8;

    public static string Read(ref RlpReader reader)
    {
        ReadOnlySpan<byte> obj = reader.ReadBytes();
        return Encoding.UTF8.GetString(obj);
    }

    public static void Write(ref RlpWriter writer, string value)
    {
        ReadOnlySpan<char> charSpan = value.AsSpan();
        var valueByteLength = Encoding.GetMaxByteCount(charSpan.Length);

        byte[]? sharedBuffer = null;
        try
        {
            Span<byte> buffer = valueByteLength <= MaxStackSize
                ? stackalloc byte[valueByteLength]
                : sharedBuffer = ArrayPool<byte>.Shared.Rent(valueByteLength);

            var bytes = Encoding.GetBytes(charSpan, buffer);

            writer.Write(buffer[..bytes]);
        }
        finally
        {
            if (sharedBuffer is not null) ArrayPool<byte>.Shared.Return(sharedBuffer);
        }
    }
}

public static class StringRlpConverterExt
{
    public static string ReadString(this ref RlpReader reader) => StringRlpConverter.Read(ref reader);
    public static void Write(this ref RlpWriter writer, string value) => StringRlpConverter.Write(ref writer, value);
}
