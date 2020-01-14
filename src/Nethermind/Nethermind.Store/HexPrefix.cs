//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public abstract class HexPrefix
    {
        sealed class ArrayBasedPrefix : HexPrefix
        {
            readonly byte[] _payload;

            public ArrayBasedPrefix(bool isLeaf, ReadOnlySpan<byte> value)
            {
                _payload = value.ToArray();
                IsLeaf = isLeaf;
            }

            public override ReadOnlySpan<byte> Path => _payload;
            public override bool IsLeaf { get; }
        }

        sealed class LongBasedPrefix : HexPrefix
        {
            public const int MaxLength = 7;
            const long IsLeafMask = 8;
            const long LengthMask = 7;

            long _value;

            public override ReadOnlySpan<byte> Path => MemoryMarshal
                .AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _value, 1))
                .Slice(1, (int)(_value & LengthMask));

            public LongBasedPrefix(bool isLeaf, ReadOnlySpan<byte> value)
            {
                _value = value.Length;
                if (isLeaf)
                {
                    _value |= IsLeafMask;
                }

                ref var destination = ref Unsafe.Add(ref Unsafe.As<long, byte>(ref _value), 1);

                for (var i = 0; i < value.Length; i++)
                {
                    Unsafe.Add(ref destination, i) = value[i];
                }
            }

            public override bool IsLeaf => (_value & IsLeafMask) != 0;
        }

        public static HexPrefix Create(bool isLeaf) => Create(isLeaf, Span<byte>.Empty);

        public static HexPrefix Create(bool isLeaf, byte path) => Create(isLeaf, MemoryMarshal.CreateReadOnlySpan(ref path, 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HexPrefix Create(bool isLeaf, ReadOnlySpan<byte> path)
        {
            if (path.Length <= LongBasedPrefix.MaxLength)
            {
                return new LongBasedPrefix(isLeaf, path);
            }

            return new ArrayBasedPrefix(isLeaf, path);
        }

        public static HexPrefix Create(bool isLeaf, ReadOnlySpan<byte> path1, ReadOnlySpan<byte> path2)
        {
            Span<byte> path = stackalloc byte[path1.Length + path2.Length];
            path1.CopyTo(path);
            path2.CopyTo(path.Slice(path1.Length));

            return Create(isLeaf, path);
        }
        
        public static HexPrefix Create(bool isLeaf, byte path1, ReadOnlySpan<byte> path2)
        {
            Span<byte> path = stackalloc byte[1 + path2.Length];
            path[0] = path1;
            path2.CopyTo(path.Slice(1));

            return Create(isLeaf, path);
        }

        public abstract ReadOnlySpan<byte> Path { get; }
        public abstract bool IsLeaf { get; }
        public bool IsExtension => !IsLeaf;

        public byte[] ToBytes()
        {
            byte[] output = new byte[Path.Length / 2 + 1];
            output[0] = (byte)(IsLeaf ? 0x20 : 0x000);
            if (Path.Length % 2 != 0)
            {
                output[0] += (byte)(0x10 + Path[0]);
            }

            for (int i = 0; i < Path.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    Path.Length % 2 == 0
                        ? (byte)(16 * Path[i] + Path[i + 1])
                        : (byte)(16 * Path[i + 1] + Path[i + 2]);
            }

            return output;
        }

        public static HexPrefix FromBytes(Span<byte> bytes)
        {
            bool isLeaf = bytes[0] >= 32;

            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);

            Span<byte> path = stackalloc byte[nibblesCount];

            ref var p = ref MemoryMarshal.GetReference(path);

            for (int i = 0; i < nibblesCount; i++)
            {
                Unsafe.Add(ref p, i) =
                path[i] =
                    isEven
                        ? i % 2 == 0
                            ? (byte)((bytes[1 + i / 2] & 240) / 16)
                            : (byte)(bytes[1 + i / 2] & 15)
                        : i % 2 == 0
                            ? (byte)(bytes[i / 2] & 15)
                            : (byte)((bytes[1 + i / 2] & 240) / 16);
            }

            return Create(isLeaf, path);
        }

        public override string ToString()
        {
            return ToBytes().ToHexString(false);
        }
    }
}
