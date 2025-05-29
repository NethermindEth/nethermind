// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Experimental.Abi.V2;

// NOTE: Make `partial` to allow for extension
public static class AbiType
{
    public static readonly IAbi<UInt32> UInt32 = new()
    {
        Name = "uint32",
        Read = r =>
        {
            var bytes = r.ReadBytes(32);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes[^sizeof(UInt32)..]);
        },
        Write = (w, v) =>
        {
            Span<byte> padding = stackalloc byte[32 - sizeof(UInt32)];
            w.Write(padding);
            Span<byte> bytes = stackalloc byte[sizeof(UInt32)];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
            w.Write(bytes);
        }
    };

    public static readonly IAbi<Boolean> Bool = new()
    {
        Name = "bool",
        Read = r => UInt32.Read(r) != 0,
        Write = (w, v) => UInt32.Write(w, v ? 1u : 0u)
    };

    public static IAbi<(T1, T2)> Tuple<T1, T2>(IAbi<T1> abi1, IAbi<T2> abi2) => new()
    {
        Name = $"({abi1.Name},{abi2.Name}))",
        Read = r => (abi1.Read(r), abi2.Read(r)),
        Write = (w, tuple) =>
        {
            abi1.Write(w, tuple.Item1);
            abi2.Write(w, tuple.Item2);
        }
    };
}
