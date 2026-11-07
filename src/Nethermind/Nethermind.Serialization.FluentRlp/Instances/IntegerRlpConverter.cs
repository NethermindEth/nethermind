// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts a non-negative <see cref="short"/>.</summary>
public abstract class Int16RlpConverter : IRlpConverter<Int16>
{
    /// <inheritdoc/>
    public static Int16 Read(ref RlpReader reader) => reader.ReadInteger<Int16>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, Int16 value) => writer.Write(value);
}

/// <summary>Converts a non-negative <see cref="int"/>.</summary>
public abstract class Int32RlpConverter : IRlpConverter<Int32>
{
    /// <inheritdoc/>
    public static Int32 Read(ref RlpReader reader) => reader.ReadInteger<Int32>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, Int32 value) => writer.Write(value);
}

/// <summary>Converts a non-negative <see cref="long"/>.</summary>
public abstract class Int64RlpConverter : IRlpConverter<Int64>
{
    /// <inheritdoc/>
    public static Int64 Read(ref RlpReader reader) => reader.ReadInteger<Int64>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, Int64 value) => writer.Write(value);
}

/// <summary>Converts a non-negative <see cref="Int128"/>.</summary>
public abstract class Int128RlpConverter : IRlpConverter<Int128>
{
    /// <inheritdoc/>
    public static Int128 Read(ref RlpReader reader) => reader.ReadInteger<Int128>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, Int128 value) => writer.Write(value);
}

/// <summary>Converts a <see cref="byte"/>.</summary>
public abstract class ByteRlpConverter : IRlpConverter<Byte>
{
    /// <inheritdoc/>
    public static Byte Read(ref RlpReader reader) => reader.ReadInteger<Byte>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, Byte value) => writer.Write(value);
}

/// <summary>Converts a <see cref="ushort"/>.</summary>
public abstract class UInt16RlpConverter : IRlpConverter<UInt16>
{
    /// <inheritdoc/>
    public static UInt16 Read(ref RlpReader reader) => reader.ReadInteger<UInt16>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, UInt16 value) => writer.Write(value);
}

/// <summary>Converts a <see cref="uint"/>.</summary>
public abstract class UInt32RlpConverter : IRlpConverter<UInt32>
{
    /// <inheritdoc/>
    public static UInt32 Read(ref RlpReader reader) => reader.ReadInteger<UInt32>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, UInt32 value) => writer.Write(value);
}

/// <summary>Converts a <see cref="ulong"/>.</summary>
public abstract class UInt64RlpConverter : IRlpConverter<UInt64>
{
    /// <inheritdoc/>
    public static UInt64 Read(ref RlpReader reader) => reader.ReadInteger<UInt64>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, UInt64 value) => writer.Write(value);
}

/// <summary>Converts a <see cref="UInt128"/>.</summary>
public abstract class UInt128RlpConverter : IRlpConverter<UInt128>
{
    /// <inheritdoc/>
    public static UInt128 Read(ref RlpReader reader) => reader.ReadInteger<UInt128>();

    /// <inheritdoc/>
    public static void Write(ref RlpWriter writer, UInt128 value) => writer.Write(value);
}

/// <summary>Named read methods for the integer types.</summary>
/// <remarks>The generic <see cref="RlpWriter.Write{T}(T)"/> covers the write side.</remarks>
public static class IntegerRlpConverterExt
{
    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static Int16 ReadInt16(this ref RlpReader reader) => reader.ReadInteger<Int16>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static Int32 ReadInt32(this ref RlpReader reader) => reader.ReadInteger<Int32>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static Int64 ReadInt64(this ref RlpReader reader) => reader.ReadInteger<Int64>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static Int128 ReadInt128(this ref RlpReader reader) => reader.ReadInteger<Int128>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static Byte ReadByte(this ref RlpReader reader) => reader.ReadInteger<Byte>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static UInt16 ReadUInt16(this ref RlpReader reader) => reader.ReadInteger<UInt16>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static UInt32 ReadUInt32(this ref RlpReader reader) => reader.ReadInteger<UInt32>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static UInt64 ReadUInt64(this ref RlpReader reader) => reader.ReadInteger<UInt64>();

    /// <inheritdoc cref="RlpReader.ReadInteger{T}"/>
    public static UInt128 ReadUInt128(this ref RlpReader reader) => reader.ReadInteger<UInt128>();
}
