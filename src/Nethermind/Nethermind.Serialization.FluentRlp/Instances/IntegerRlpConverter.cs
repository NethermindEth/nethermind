// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FluentRlp.Instances;

public abstract class Int16RlpConverter : IRlpConverter<Int16>
{
    public static Int16 Read(ref RlpReader reader) => reader.ReadInteger<Int16>();

    public static void Write(ref RlpWriter writer, Int16 value) => writer.Write(value);
}

public abstract class Int32RlpConverter : IRlpConverter<Int32>
{
    public static Int32 Read(ref RlpReader reader) => reader.ReadInteger<Int32>();

    public static void Write(ref RlpWriter writer, Int32 value) => writer.Write(value);
}

public abstract class Int64RlpConverter : IRlpConverter<Int64>
{
    public static Int64 Read(ref RlpReader reader) => reader.ReadInteger<Int64>();

    public static void Write(ref RlpWriter writer, Int64 value) => writer.Write(value);
}

public abstract class Int128RlpConverter : IRlpConverter<Int128>
{
    public static Int128 Read(ref RlpReader reader) => reader.ReadInteger<Int128>();

    public static void Write(ref RlpWriter writer, Int128 value) => writer.Write(value);
}

public abstract class UInt16RlpConverter : IRlpConverter<UInt16>
{
    public static UInt16 Read(ref RlpReader reader) => reader.ReadInteger<UInt16>();

    public static void Write(ref RlpWriter writer, UInt16 value) => writer.Write(value);
}

public abstract class UInt32RlpConverter : IRlpConverter<UInt32>
{
    public static UInt32 Read(ref RlpReader reader) => reader.ReadInteger<UInt32>();

    public static void Write(ref RlpWriter writer, UInt32 value) => writer.Write(value);
}

public abstract class UInt64RlpConverter : IRlpConverter<UInt64>
{
    public static UInt64 Read(ref RlpReader reader) => reader.ReadInteger<UInt64>();

    public static void Write(ref RlpWriter writer, UInt64 value) => writer.Write(value);
}

public abstract class UInt128RlpConverter : IRlpConverter<UInt128>
{
    public static UInt128 Read(ref RlpReader reader) => reader.ReadInteger<UInt128>();

    public static void Write(ref RlpWriter writer, UInt128 value) => writer.Write(value);
}

// NOTE: No need for `Write` overloads since they're covered by generic primitives
// `Read` methods are provided for a consistent API (instead of using generics primitives)
public static class IntegerRlpConverterExt
{
    public static Int16 ReadInt16(this ref RlpReader reader) => reader.ReadInteger<Int16>();
    public static Int32 ReadInt32(this ref RlpReader reader) => reader.ReadInteger<Int32>();
    public static Int64 ReadInt64(this ref RlpReader reader) => reader.ReadInteger<Int64>();
    public static Int128 ReadInt128(this ref RlpReader reader) => reader.ReadInteger<Int128>();
    public static UInt16 ReadUInt16(this ref RlpReader reader) => reader.ReadInteger<UInt16>();
    public static UInt64 UInt64(this ref RlpReader reader) => reader.ReadInteger<UInt64>();
    public static UInt64 ReadUInt64(this ref RlpReader reader) => reader.ReadInteger<UInt64>();
    public static UInt128 UReadInt128(this ref RlpReader reader) => reader.ReadInteger<UInt128>();
}
