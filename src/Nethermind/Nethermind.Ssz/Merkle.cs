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
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

        public static uint NextPowerOfTwoExponent(uint v)
        {
            if (Lzcnt.IsSupported)
            {
                return 32 - Lzcnt.LeadingZeroCount(--v);
            }

            throw new NotImplementedException();
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

        private static Chunk Compute(Span<Chunk> span)
        {
            return MemoryMarshal.Cast<byte, Chunk>(Sha256.Compute(MemoryMarshal.Cast<Chunk, byte>(span)).Bytes)[0];
        }

        private static Chunk HashConcatenation(Chunk left, Chunk right, int level)
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

        private static void MixIn(Span<byte> span, int value)
        {
            Span<byte> valueBytes = stackalloc byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(valueBytes.Slice(0, 4), value);
            Chunk result = HashConcatenation(MemoryMarshal.Cast<byte, Chunk>(span)[0], MemoryMarshal.Cast<byte, Chunk>(valueBytes)[0], 0);
            result.ToLittleEndian(span);
        }

        public static void Ize(Span<byte> root, bool value)
        {
            root[0] = Ssz.Encode(value);
        }
        
        public static void Ize(Span<byte> root, byte value)
        {
            Ssz.Encode(root.Slice(0, 1), value);
        }

        public static void Ize(Span<byte> root, ushort value)
        {
            Ssz.Encode(root.Slice(0, 2), value);
        }

        public static void Ize(Span<byte> root, int value)
        {
            Ize(root, (uint) value);
        }
        
        public static void Ize(Span<byte> root, uint value)
        {
            Ssz.Encode(root.Slice(0, 4), value);
        }

        public static void Ize(Span<byte> root, ulong value)
        {
            Ssz.Encode(root.Slice(0, 8), value);
        }

        public static void Ize(Span<byte> root, UInt128 value)
        {
            Ssz.Encode(root.Slice(0, 16), value);
        }

        public static void Ize(Span<byte> root, UInt256 value)
        {
            Ssz.Encode(root, value);
        }

        public static void Ize(Span<byte> root, Span<bool> value)
        {
            const int typeSize = 1;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<bool> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<bool> lastChunk = stackalloc bool[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(root, MemoryMarshal.Cast<bool, Chunk>(fullChunks), MemoryMarshal.Cast<bool, Chunk>(lastChunk));
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<bool, Chunk>(value), Span<Chunk>.Empty);
            }
        }

        public static void Ize(Span<byte> root, Span<byte> value)
        {
            const int typeSize = 1;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<byte> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<byte> lastChunk = stackalloc byte[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(root, MemoryMarshal.Cast<byte, Chunk>(fullChunks), MemoryMarshal.Cast<byte, Chunk>(lastChunk));
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<byte, Chunk>(value), Span<Chunk>.Empty);
            }
        }

        public static void IzeBits(Span<byte> root, Span<byte> value, uint limit)
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
                Ize(root, MemoryMarshal.Cast<byte, Chunk>(fullChunks), MemoryMarshal.Cast<byte, Chunk>(lastChunk), limit);
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<byte, Chunk>(value), Span<Chunk>.Empty, limit);
            }

            MixIn(root, length);
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

        public static void Ize(Span<byte> root, Span<ushort> value)
        {
            const int typeSize = 2;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<ushort> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<ushort> lastChunk = stackalloc ushort[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(root, MemoryMarshal.Cast<ushort, Chunk>(fullChunks), MemoryMarshal.Cast<ushort, Chunk>(lastChunk));
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<ushort, Chunk>(value), Span<Chunk>.Empty);
            }
        }

        public static void Ize(Span<byte> root, Span<uint> value)
        {
            const int typeSize = 4;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<uint> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<uint> lastChunk = stackalloc uint[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(root, MemoryMarshal.Cast<uint, Chunk>(fullChunks), MemoryMarshal.Cast<uint, Chunk>(lastChunk));
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<uint, Chunk>(value), Span<Chunk>.Empty);
            }
        }

        public static void Ize(Span<byte> root, Span<ulong> value)
        {
            const int typeSize = 8;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<ulong> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<ulong> lastChunk = stackalloc ulong[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(root, MemoryMarshal.Cast<ulong, Chunk>(fullChunks), MemoryMarshal.Cast<ulong, Chunk>(lastChunk));
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<ulong, Chunk>(value), Span<Chunk>.Empty);
            }
        }

        public static void Ize(Span<byte> root, Span<UInt128> value)
        {
            const int typeSize = 16;
            int partialChunkLength = value.Length % (32 / typeSize);
            if (partialChunkLength > 0)
            {
                Span<UInt128> fullChunks = value.Slice(0, value.Length - partialChunkLength);
                Span<UInt128> lastChunk = stackalloc UInt128[32 / typeSize];
                value.Slice(value.Length - partialChunkLength).CopyTo(lastChunk);
                Ize(root, MemoryMarshal.Cast<UInt128, Chunk>(fullChunks), MemoryMarshal.Cast<UInt128, Chunk>(lastChunk));
            }
            else
            {
                Ize(root, MemoryMarshal.Cast<UInt128, Chunk>(value), Span<Chunk>.Empty);
            }
        }

        public static void Ize(Span<byte> root, Span<UInt256> value)
        {
            Ize(root, value, Span<UInt256>.Empty);
        }

        private static void Ize(Span<byte> root, Span<Chunk> value, Span<Chunk> lastChunk, uint limit = 0)
        {
            // everything here (including last chunk) is designed for zero allocation
            // the last chunk introduces a lot of additional complexity
            
            int nonVirtualChunksCount = value.Length + lastChunk.Length;
            uint chunkCount = NextPowerOfTwo(limit != 0 ? limit : (uint) nonVirtualChunksCount);

            int level = 0;
            Chunk left = ZeroHashes[0];
            Chunk right = ZeroHashes[0];

            Span<Chunk> result = stackalloc UInt256[1];
            if (nonVirtualChunksCount == 0)
            {
                // if everything is virtual then we just need to find the right level of zero-hash
                uint exponent = NextPowerOfTwoExponent(limit != 0 ? limit : (uint) nonVirtualChunksCount);
                result[0] = ZeroHashes[exponent];
            }
            else
            {
                while (chunkCount > 1)
                {
                    int position = 0;
                    while (position < chunkCount - 1)
                    {
                        // we pick left and right between value position, last chunk or virtual (zero-hash)
                        if (position < value.Length - 1)
                        {
                            left = value[position];
                            right = value[position + 1];
                        }
                        else if (position == value.Length - 1)
                        {
                            left = value[position];
                            right = lastChunk.IsEmpty ? ZeroHashes[level] : lastChunk[0];
                            if (value.Length > 0) // if no value then we use the last chunk for result storage
                            {
                                lastChunk = Span<Chunk>.Empty; // we read it only once and will not use for result storage
                            }
                        }
                        else if (position == value.Length)
                        {
                            left = lastChunk.IsEmpty ? ZeroHashes[level] : lastChunk[0];
                            right = ZeroHashes[level];
                            if (value.Length > 0) // if no value then we use the last chunk for result storage
                            {
                                lastChunk = Span<Chunk>.Empty; // we read it only once and will not use for result storage
                            }
                        }
                        else if (position > value.Length)
                        {
                            left = ZeroHashes[level];
                            right = ZeroHashes[level];
                        }

                        Chunk hash = HashConcatenation(left, right, level);
                        if (value.Length == 0 && position == 0)
                        {
                            // here we use the last chunk for result storage
                            // we just need one chunk as with no value chunks all other chunks are virtual
                            // and can be zero-hashed 
                            lastChunk[0] = hash;
                        }
                        else if (value.Length > position / 2)
                        {
                            // we are overwriting the first half of the chunks with the next level
                            value[position / 2] = hash;
                        }

                        // we hash two items at the time for the binary tree
                        position += 2;
                    }

                    // we will consider only the first half of the chunks for the next level
                    // we stored there the results from hashing
                    chunkCount >>= 1;
                    level++;
                }

                // by default we store result in the first value chunk or the last chunk if the value is empty
                result = value.Length > 0 ? value.Slice(0, 1) : lastChunk;
            }

            MemoryMarshal.Cast<Chunk, byte>(result).CopyTo(root);
        }
    }
}