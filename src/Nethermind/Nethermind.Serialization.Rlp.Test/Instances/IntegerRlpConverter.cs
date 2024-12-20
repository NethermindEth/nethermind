// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.Test.Instances;

// NOTE: No need for `Write` overloads since they're covered by generic primitives
// `Read` methods are provided for a consistent API (instead of using generics primitives)
public static class IntegerRlpConverterExt
{
    public static Int16 ReadInt16(this ref RlpReader reader) => reader.ReadInteger<Int16>();
    public static Int32 ReadInt32(this ref RlpReader reader) => reader.ReadInteger<Int32>();
    public static Int64 ReadInt64(this ref RlpReader reader) => reader.ReadInteger<Int64>();
    public static Int128 ReadInt128(this ref RlpReader reader) => reader.ReadInteger<Int128>();
}
