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
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core2;
using Nethermind.Dirichlet.Numerics;
using Chunk = Nethermind.Dirichlet.Numerics.UInt256;

namespace Nethermind.Ssz
{
    public class Merkle
    {
        public static UInt256[] ZeroHashes = new UInt256[64];

        private static void BuildZeroHashes()
        {
            Span<UInt256> concatenation = stackalloc UInt256[2];
            UInt256.CreateFromLittleEndian(out ZeroHashes[0], Sha256.Zero.Bytes);
            for (int i = 1; i < 64; i++)
            {
                var previous = ZeroHashes[i - 1];
                MemoryMarshal.CreateSpan(ref previous, 1).CopyTo(concatenation.Slice(0, 1));
                MemoryMarshal.CreateSpan(ref previous, 1).CopyTo(concatenation.Slice(1, 1));
                UInt256.CreateFromLittleEndian(out ZeroHashes[i], Sha256.Compute(MemoryMarshal.Cast<UInt256, byte>(concatenation)).Bytes);
            }
        }

        static Merkle()
        {
            BuildZeroHashes();
        }

        [Todo(Improve.Refactor, "Consider moving to extensions")]
        public static uint NextPowerOfTwo(uint v)
        {
            if (Lzcnt.IsSupported)
            {
                return (uint) 1 << (int) (32 - Lzcnt.LeadingZeroCount(--v));
            }

            if (v != 0U) v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;

            return v;
        }

        [Todo(Improve.Refactor, "Consider moving to extensions")]
        public static ulong NextPowerOfTwo(ulong v)
        {
            if (Lzcnt.IsSupported)
            {
                return (ulong) 1 << (int) (64 - Lzcnt.X64.LeadingZeroCount(--v));
            }

            if (v != 0UL) v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v |= v >> 32;
            v++;

            return v;
        }

        [Todo(Improve.Performance, "value Sha256")]
        public static Sha256 HashConcatenation(Span<byte> span1, Span<byte> span2, int level)
        {
#if DEBUG
            if (span1.Length != 32 || + span2.Length != 32)
            {
                throw new InvalidOperationException($"{nameof(HashConcatenation)} should only operate on two spans of 32 bytes each");
            }
#endif

            Span<byte> concatenation = stackalloc byte[64];
            span1.CopyTo(concatenation.Slice(0, 32));
            span2.CopyTo(concatenation.Slice(32, 32));
            return Sha256.Compute(concatenation);
        }

        private static Chunk Compute(Span<Chunk> span)
        {
            return MemoryMarshal.Cast<byte, Chunk>(Sha256.Compute(MemoryMarshal.Cast<Chunk, byte>(span)).Bytes)[0];
        }
        
        public static Chunk HashConcatenation(Chunk left, Chunk right, int level)
        {
            if (IsZeroHash(left, level) && IsZeroHash(right, level))
            {
                return ZeroHashes[level + 1];
            }
            
            Span<Chunk> concatenation = stackalloc Chunk[2];
            concatenation[0] = left;
            concatenation[1] = right;
            return Compute(concatenation);
        }

        public static bool IsZeroHash(Chunk span, int level)
        {
            return span.Equals(ZeroHashes[level]);
        }
        
        public static Sha256 Mix(Span<byte> span, UInt256 value, int level)
        {
            Span<byte> valueBytes = stackalloc byte[32];
            value.ToLittleEndian(valueBytes);
            return HashConcatenation(span, valueBytes, level);
        }
        
        public static void Ize(Span<byte> root, byte value)
        {
            Ssz.Encode(root.Slice(31, 1), value);
        }
        
        public static void Ize(Span<byte> root, ushort value)
        {
            Ssz.Encode(root.Slice(30, 2), value);
        }
        
        public static void Ize(Span<byte> root, uint value)
        {
            Ssz.Encode(root.Slice(28, 4), value);
        }
        
        public static void Ize(Span<byte> root, ulong value)
        {
            Ssz.Encode(root.Slice(24, 8), value);
        }
        
        public static void Ize(Span<byte> root, UInt128 value)
        {
            Ssz.Encode(root.Slice(16, 16), value);
        }
        
        public static void Ize(Span<byte> root, UInt256 value)
        {
            Ssz.Encode(root, value);
        }
        
        public static void Ize(Span<byte> root, Span<byte> value)
        {
            Ize(root, MemoryMarshal.Cast<byte, Chunk>(value));
        }
        
        public static void Ize(Span<byte> root, Span<ushort> value)
        {
            Ize(root, MemoryMarshal.Cast<ushort, Chunk>(value));
        }
        
        public static void Ize(Span<byte> root, Span<uint> value)
        {
            Ize(root, MemoryMarshal.Cast<uint, Chunk>(value));
        }
        
        public static void Ize(Span<byte> root, Span<ulong> value)
        {
            Ize(root, MemoryMarshal.Cast<ulong, Chunk>(value));
        }
        
        public static void Ize(Span<byte> root, Span<UInt128> value)
        {
            Ize(root, MemoryMarshal.Cast<UInt128, Chunk>(value));
        }
        
        public static void Ize(Span<byte> root, Span<Chunk> value)
        {
            uint chunkCount = NextPowerOfTwo((uint) value.Length);
            int level = 0;
            int position = 0;
            while (chunkCount != 1)
            {
                while (position < chunkCount - 1)
                {
                    Chunk hash = HashConcatenation(value[position], value[position + 1], level);
                    value[position] = hash;
                    position += 2;
                }

                chunkCount >>= 1;
                level++;
            }

            MemoryMarshal.Cast<Chunk, byte>(value.Slice(0, 1)).CopyTo(root);
        }
    }
}