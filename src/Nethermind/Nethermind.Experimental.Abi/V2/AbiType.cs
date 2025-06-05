// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Experimental.Abi.V2;

// NOTE: Make `partial` to allow for extension
public static class AbiType
{
    private static int PadTo32(int length)
    {
        int rem = length % 32;
        return rem == 0 ? length : length + (32 - rem);
    }

    public static readonly IAbi<UInt256> UInt256 = new()
    {
        Name = "uint256",
        Read = r =>
        {
            var bytes = r.ReadBytes(32);
            return new UInt256(bytes, isBigEndian: true);
        },
        Write = (w, v) =>
        {
            Span<byte> bytes = stackalloc byte[32];
            v.ToBigEndian(bytes);
            w.Write(bytes);
        }
    };

    public static readonly IAbi<UInt64> UInt64 = new()
    {
        Name = "uint64",
        Read = r => (UInt64)UInt256.Read(r),
        Write = (w, v) => UInt256.Write(w, v),
    };

    public static readonly IAbi<UInt32> UInt32 = new()
    {
        Name = "uint32",
        Read = r => (UInt32)UInt256.Read(r),
        Write = (w, v) => UInt256.Write(w, v),
    };

    public static readonly IAbi<UInt16> UInt16 = new()
    {
        Name = "uint16",
        Read = r => (UInt16)UInt256.Read(r),
        Write = (w, v) => UInt256.Write(w, v),
    };

    public static readonly IAbi<Byte> UInt8 = new()
    {
        Name = "uint8",
        Read = r => (Byte)UInt256.Read(r),
        Write = (w, v) => UInt256.Write(w, v),
    };

    public static readonly IAbi<Boolean> Bool = new()
    {
        Name = "bool",
        Read = r => UInt256.Read(r) != 0,
        Write = (w, v) => UInt256.Write(w, v ? 1u : 0u)
    };

    public static IAbi<T[]> Array<T>(IAbi<T> elements, int length) => new()
    {
        Name = $"{elements.Name}[{length}]",
        Read = r =>
        {
            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = elements.Read(r);
            }
            return array;
        },
        Write = (w, array) =>
        {
            if (array.Length != length) throw new AbiException();

            foreach (var item in array)
            {
                elements.Write(w, item);
            }
        }
    };

    public static IAbi<byte[]> BytesM(int length) => new()
    {
        Name = $"bytes{length}",
        Read = r =>
        {
            var bytes = r.ReadBytes(length);

            var padding = PadTo32(length) - length;
            if (padding > 0)
            {
                r.ReadBytes(padding); // Skip padding bytes
            }

            return bytes;
        },
        Write = (w, bytes) =>
        {
            w.Write(bytes);

            var padding = PadTo32(length) - bytes.Length;
            if (padding > 0)
            {
                w.Write(stackalloc byte[padding]);
            }
        }
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
