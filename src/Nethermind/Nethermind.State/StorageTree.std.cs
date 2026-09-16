// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    public partial class StorageTree
    {
        private const int LookupSize = 1024;

        /// <summary>Hashed trie keys for storage slots <c>0</c> to <see cref="LookupSize"/> - 1.</summary>
        /// <remarks>
        /// Built up front: a long-lived process amortises the cost over every block it goes on to
        /// process. See <c>StorageTree.zkevm.cs</c> for the guest form.
        /// </remarks>
        private static readonly ValueHash256[] Lookup = CreateLookup();

        [InlineArray(34)]
        private struct LookupHashBuffer
        {
            private Vector256<byte> _element0;
        }

        private static ValueHash256[] CreateLookup()
        {
            ValueHash256[] lookup = new ValueHash256[LookupSize];
            if (Avx2.IsSupported)
            {
                int rate = Avx512F.IsSupported ? Keccak.Size : 136;
                LookupHashBuffer buffer = default;
                Span<byte> blocks = MemoryMarshal.AsBytes((Span<Vector256<byte>>)buffer);
                Span<byte> hashes = MemoryMarshal.AsBytes(lookup.AsSpan());
                int batchSize = Avx512F.IsSupported ? 8 : 4;
                for (int lane = 0; !Avx512F.IsSupported && lane < batchSize; lane++)
                {
                    blocks[lane * rate + Keccak.Size] = 1;
                    blocks[lane * rate + rate - 1] = 128;
                }
                // LookupSize is a multiple of both batch widths, so every output batch fits.
                for (int i = 0; i < lookup.Length; i += batchSize)
                {
                    for (int lane = 0; lane < batchSize; lane++)
                        BinaryPrimitives.WriteUInt32BigEndian(blocks.Slice(lane * rate + Keccak.Size - sizeof(uint), sizeof(uint)), (uint)(i + lane));
                    if (Avx512F.IsSupported)
                        KeccakHash.ComputeHash32Bytes8Avx512(ref blocks[0], ref hashes[i * Keccak.Size]);
                    else
                        KeccakHash.ComputePaddedBlocks4Avx2(ref blocks[0], ref hashes[i * Keccak.Size]);
                }
            }
            else
            {
                ValueHash256 buffer = default;
                for (int i = 0; i < lookup.Length; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.BytesAsSpan[(Keccak.Size - sizeof(uint))..], (uint)i);
                    lookup[i] = ValueKeccak.Compute(buffer.Bytes);
                }
            }

            return lookup;
        }
    }
}
