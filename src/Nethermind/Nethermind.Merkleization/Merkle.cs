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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using Chunk = Nethermind.Dirichlet.Numerics.UInt256;

namespace Nethermind.Merkleization
{
    public static partial class Merkle
    {
        public static UInt256[] ZeroHashes = new UInt256[64];

        private static void BuildZeroHashes()
        {
            Span<UInt256> concatenation = stackalloc UInt256[2];
            // ZeroHashes[0] will be UInt256.Zero
            for (int i = 1; i < 64; i++)
            {
                var previous = ZeroHashes[i - 1];
                MemoryMarshal.CreateSpan(ref previous, 1).CopyTo(concatenation.Slice(0, 1));
                MemoryMarshal.CreateSpan(ref previous, 1).CopyTo(concatenation.Slice(1, 1));
                UInt256.CreateFromLittleEndian(out ZeroHashes[i], Sha256.Compute(MemoryMarshal.Cast<UInt256, byte>(concatenation)).AsSpan().ToArray());
            }
        }

        static Merkle()
        {
            BuildZeroHashes();
            UInt256.CreateFromBigEndian(out RootOfNull, Sha256.RootOfAnEmptyString.AsSpan().ToArray());
        }

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

        public static int NextPowerOfTwoExponent(ulong v)
        {
            if (v == 0)
            {
                return 0;
            }
            
            int leadingZeros = 0;
            if (Lzcnt.IsSupported)
            {
                leadingZeros = (int) Lzcnt.X64.LeadingZeroCount(--v);
            }
            else
            {
                leadingZeros = CountLeadingZeros(v);
            }

            return 64 - leadingZeros;
        }

        private static int CountLeadingZeros(ulong x)
        {
            x--;
            
            int count = 0;
            for (int i = 63; i >= 0; i--)
            {
                if (x / (1UL << i) == 1)
                {
                    break;
                }
                
                count++;
            }

            return count;
        }

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

        private static Chunk Compute(Span<Chunk> span)
        {
            return MemoryMarshal.Cast<byte, Chunk>(Sha256.ComputeBytes(MemoryMarshal.Cast<Chunk, byte>(span)))[0];
        }

        internal static Chunk HashConcatenation(Chunk left, Chunk right, int level)
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

        private static bool IsZeroHash(Chunk span, int level)
        {
            return span.Equals(ZeroHashes[level]);
        }

        public static void MixIn(ref UInt256 root, int value)
        {
            UInt256.Create(out UInt256 lengthPart, value);
            root = HashConcatenation(root, lengthPart, 0);
        }

        public static void Ize(out UInt256 root, bool value)
        {
            root = value ? UInt256.One : UInt256.Zero;
        }

        public static void Ize(out UInt256 root, byte value)
        {
            UInt256.Create(out root, value);
        }

        public static void Ize(out UInt256 root, ushort value)
        {
            UInt256.Create(out root, value);
        }

        public static void Ize(out UInt256 root, int value)
        {
            UInt256.Create(out root, value);
        }

        public static void Ize(out UInt256 root, uint value)
        {
            UInt256.Create(out root, value);
        }

        public static void Ize(out UInt256 root, ulong value)
        {
            UInt256.Create(out root, value);
        }

        public static void Ize(out UInt256 root, UInt128 value)
        {
            UInt256.Create(out root, value);
        }

        public static void Ize(out UInt256 root, UInt256 value)
        {
            root = value;
        }

        public static void Ize(out UInt256 root, Bytes32 value)
        {
            ReadOnlySpan<byte> readOnlyBytes = value.AsSpan();
            unsafe
            {
                fixed (byte* buffer = &readOnlyBytes.GetPinnableReference())
                {
                    Span<byte> apiNeedsWriteableEvenThoughOnlyReading = new Span<byte>(buffer, readOnlyBytes.Length);
                    UInt256.CreateFromLittleEndian(out root, apiNeedsWriteableEvenThoughOnlyReading);
                }
            }
        }

        public static void Ize(out UInt256 root, Root value)
        {
            ReadOnlySpan<byte> readOnlyBytes = value.AsSpan();
            unsafe
            {
                fixed (byte* buffer = &readOnlyBytes.GetPinnableReference())
                {
                    Span<byte> apiNeedsWriteableEvenThoughOnlyReading = new Span<byte>(buffer, readOnlyBytes.Length);
                    UInt256.CreateFromLittleEndian(out root, apiNeedsWriteableEvenThoughOnlyReading);
                }
            }
        }

        public static void Ize(out UInt256 root, Span<bool> value)
        {
            const int typeSize = 1;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<bool> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<bool> lastChunk = stackalloc bool[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<bool, Chunk>(fullChunks), MemoryMarshal.Cast<bool, Chunk>(lastChunk));
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<bool, Chunk>(value));
            }
        }

        public static void Ize(out UInt256 root, Span<byte> value)
        {
            const int typeSize = 1;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<byte> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<byte> lastChunk = stackalloc byte[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<byte, Chunk>(fullChunks), MemoryMarshal.Cast<byte, Chunk>(lastChunk));
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<byte, Chunk>(value));
            }
        }
        
        public static void Ize(out UInt256 root, ReadOnlySpan<byte> value, ulong chunkCount)
        {
            const int typeSize = 1;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                ReadOnlySpan<byte> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<byte> lastChunk = stackalloc byte[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<byte, Chunk>(fullChunks), MemoryMarshal.Cast<byte, Chunk>(lastChunk), chunkCount);
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<byte, Chunk>(value), chunkCount);
            }
        }
        
        public static void IzeBits(out UInt256 root, Span<byte> value, uint limit)
        {
            // reset lowest bit perf
            int lastBitPosition = ResetLastBit(ref value[^1]);
            int length = value.Length * 8 - (8 - lastBitPosition);
            if (value[^1] == 0)
            {
                value = value.Slice(0, value.Length - 1);
            }

            const int typeSize = 1;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<byte> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<byte> lastChunk = stackalloc byte[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<byte, Chunk>(fullChunks), MemoryMarshal.Cast<byte, Chunk>(lastChunk), limit);
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<byte, Chunk>(value), Span<Chunk>.Empty, limit);
            }

            MixIn(ref root, length);
        }

        private static int ResetLastBit(ref byte lastByte)
        {
            if ((lastByte >> 7) % 2 == 1)
            {
                lastByte -= 128;
                return 7;
            }

            if ((lastByte >> 6) % 2 == 1)
            {
                lastByte -= 64;
                return 6;
            }

            if ((lastByte >> 5) % 2 == 1)
            {
                lastByte -= 32;
                return 5;
            }

            if ((lastByte >> 4) % 2 == 1)
            {
                lastByte -= 16;
                return 4;
            }

            if ((lastByte >> 3) % 2 == 1)
            {
                lastByte -= 8;
                return 3;
            }

            if ((lastByte >> 2) % 2 == 1)
            {
                lastByte -= 4;
                return 2;
            }

            if ((lastByte >> 1) % 2 == 1)
            {
                lastByte -= 2;
                return 1;
            }

            if (lastByte % 2 == 1)
            {
                lastByte -= 1;
                return 0;
            }

            return 8;
        }

        public static void Ize(out UInt256 root, Span<ushort> value)
        {
            const int typeSize = 2;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<ushort> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<ushort> lastChunk = stackalloc ushort[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<ushort, Chunk>(fullChunks), MemoryMarshal.Cast<ushort, Chunk>(lastChunk));
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<ushort, Chunk>(value));
            }
        }

        public static void Ize(out UInt256 root, Span<uint> value)
        {
            const int typeSize = 4;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<uint> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<uint> lastChunk = stackalloc uint[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<uint, Chunk>(fullChunks), MemoryMarshal.Cast<uint, Chunk>(lastChunk));
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<uint, Chunk>(value));
            }
        }

        public static void Ize(out UInt256 root, Span<ulong> value, ulong maxLength = 0U)
        {
            const int typeSize = sizeof(ulong);
            ulong limit = (maxLength * typeSize + 31) / 32;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<ulong> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<ulong> lastChunk = stackalloc ulong[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<ulong, Chunk>(fullChunks), MemoryMarshal.Cast<ulong, Chunk>(lastChunk), limit);
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<ulong, Chunk>(value), limit);
            }
        }

        public static void Ize(out UInt256 root, Span<UInt128> value)
        {
            const int typeSize = 16;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<UInt128> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<UInt128> lastChunk = stackalloc UInt128[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(out root, MemoryMarshal.Cast<UInt128, Chunk>(fullChunks), MemoryMarshal.Cast<UInt128, Chunk>(lastChunk));
            }
            else
            {
                Ize(out root, MemoryMarshal.Cast<UInt128, Chunk>(value));
            }
        }

        public static void Ize(out UInt256 root, ReadOnlySpan<UInt256> value, ReadOnlySpan<UInt256> lastChunk, ulong limit = 0)
        {
            if (limit == 0 && (value.Length + lastChunk.Length == 1))
            {
                root = value.Length == 0 ? lastChunk[0] : value[0];
                return;
            }

            int depth = NextPowerOfTwoExponent(limit == 0UL ? (uint) (value.Length + lastChunk.Length) : limit);
            Merkleizer merkleizer = new Merkleizer(depth);
            int length = value.Length;
            for (int i = 0; i < length; i++)
            {
                merkleizer.Feed(value[i]);
            }

            if (lastChunk.Length > 0)
            {
                merkleizer.Feed(lastChunk[0]);
            }

            merkleizer.CalculateRoot(out root);
        }

        public static void Ize(out UInt256 root, List<Ref<DepositData>> value, ulong limit)
        {
            int length = value.Count;
            if (limit == 0 && length == 1)
            {
                Merkle.Ize(out root, value[0]);
                return;
            }

            int depth = NextPowerOfTwoExponent(limit == 0UL ? (ulong) length : limit);
            Merkleizer merkleizer = new Merkleizer(depth);
            for (int i = 0; i < length; i++)
            {
                Merkle.Ize(out UInt256 subroot, value[i]);
                merkleizer.Feed(subroot);
            }

            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, List<DepositData> value, ulong limit)
        {
            int length = value.Count;
            if (limit == 0 && length == 1)
            {
                Merkle.Ize(out root, value[0]);
                return;
            }

            int depth = NextPowerOfTwoExponent(limit == 0UL ? (ulong) length : limit);
            Merkleizer merkleizer = new Merkleizer(depth);
            for (int i = 0; i < length; i++)
            {
                Merkle.Ize(out UInt256 subroot, value[i]);
                merkleizer.Feed(subroot);
            }

            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, ReadOnlySpan<UInt256> value, ulong limit = 0UL)
        {
            if (limit == 0 && value.Length == 1)
            {
                root = value[0];
                return;
            }

            int depth = NextPowerOfTwoExponent(limit == 0UL ? (ulong) value.Length : limit);
            Merkleizer merkleizer = new Merkleizer(depth);
            int length = value.Length;
            for (int i = 0; i < length; i++)
            {
                merkleizer.Feed(value[i]);
            }

            merkleizer.CalculateRoot(out root);
        }
    }
}