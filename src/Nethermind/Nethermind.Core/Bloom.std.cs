// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Core;

public partial class Bloom
{
    /// <summary>The first eight bytes of <paramref name="keccak"/>, big-endian; the bloom indexes are its top 48 bits.</summary>
    /// <remarks>See <c>Bloom.zkevm.cs</c> for the guest form.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadIndexBits(ref byte keccak) => BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref keccak));

    /// <summary>The zero-length (0 bytes long) logs bloom that EIP-7668 requires in headers and receipts.</summary>
    /// <remarks>
    /// Unlike <see cref="Empty"/> (256 zero bytes) it exposes no <see cref="Bytes"/> and RLP-encodes as <c>0x80</c>.
    /// Its unexposed storage has every bit set, so <see cref="Matches(BloomExtract)"/> matches every query without a check.
    /// Its <see cref="BloomStructRef"/> has no bytes, so struct-ref callers check <see cref="Bytes"/> for emptiness first.
    /// </remarks>
    public static readonly Bloom ZeroLength = CreateZeroLength();
}
