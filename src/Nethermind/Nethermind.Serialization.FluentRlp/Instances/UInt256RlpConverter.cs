// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts a <see cref="UInt256"/>.</summary>
public abstract class UInt256RlpConverter : IRlpConverter<UInt256>
{
    /// <inheritdoc/>
    /// <exception cref="RlpReaderException">The item is not a canonical integer of at most 32 bytes.</exception>
    public static UInt256 Read(ref RlpReader reader)
    {
        ReadOnlySpan<byte> bigEndian = reader.ReadIntegerBytes();
        if (bigEndian.Length > 32) throw new RlpReaderException("RLP integer is too large");
        return new UInt256(bigEndian, isBigEndian: true);
    }

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, UInt256 value)
    {
        Span<byte> bigEndian = stackalloc byte[32];
        value.ToBigEndian(bigEndian);
        writer.WriteIntegerBytes(bigEndian);
    }
}

/// <summary>Extension methods for <see cref="UInt256RlpConverter"/>.</summary>
public static class UInt256RlpConverterExt
{
    /// <inheritdoc cref="UInt256RlpConverter.Read"/>
    public static UInt256 ReadUInt256(this ref RlpReader reader) => UInt256RlpConverter.Read(ref reader);

    /// <inheritdoc cref="UInt256RlpConverter.Write"/>
    public static void Write(this ref RlpWriter writer, UInt256 value) => UInt256RlpConverter.Write(ref writer, value);
}
