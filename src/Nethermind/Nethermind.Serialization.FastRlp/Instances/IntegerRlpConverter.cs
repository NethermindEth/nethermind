// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FastRlp.Instances;

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

// NOTE: No need for `Write` overloads since they're covered by generic primitives
// `Read` methods are provided for a consistent API (instead of using generics primitives)
public static class IntegerRlpConverterExt
{
    public static Int16 ReadInt16(this ref RlpReader reader) => reader.ReadInteger<Int16>();
    public static Int32 ReadInt32(this ref RlpReader reader) => reader.ReadInteger<Int32>();
    public static Int64 ReadInt64(this ref RlpReader reader) => reader.ReadInteger<Int64>();
    public static Int128 ReadInt128(this ref RlpReader reader) => reader.ReadInteger<Int128>();
}
