// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    private static unsafe delegate*<in UInt256, in UInt256, out UInt256, void> _hashPairAccelerator;

    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256 into <paramref name="parent"/>, which may alias either chunk.</summary>
    /// <remarks>
    /// Uses the pair hasher the guest registered through <see cref="TryRegisterHashPairAccelerator"/>, and
    /// the SHA-256 accelerator every zkVM provides until one is registered.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void HashPair(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        delegate*<in UInt256, in UInt256, out UInt256, void> accelerator = _hashPairAccelerator;
        if (accelerator is null)
        {
            HashPairWithSha256(in left, in right, out parent);
            return;
        }

        accelerator(in left, in right, out parent);
    }

    [SkipLocalsInit]
    private static void HashPairWithSha256(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        Span<UInt256> concatenation = stackalloc UInt256[2];
        concatenation[0] = left;
        concatenation[1] = right;

        Unsafe.SkipInit(out parent);
        Accelerators.Sha256(
            MemoryMarshal.AsBytes(concatenation),
            MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref parent, 1)));
    }

    /// <summary>Writes the parent of two nodes at <paramref name="level"/> into <paramref name="parent"/>, which may alias either child.</summary>
    /// <remarks>
    /// Hashes two zero subtrees rather than looking them up as the host does: padding never forms such a pair
    /// in the level loop, so only zero chunks in the data do, and testing every pair costs the guest more than
    /// the rare hash it saves.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashNodes(in UInt256 left, in UInt256 right, int level, out UInt256 parent) =>
        HashPair(in left, in right, out parent);

    /// <summary>Routes merkleization's pair hashing through a zkVM accelerator if it hashes a known pair correctly.</summary>
    /// <param name="hashPair">
    /// Hashes the 64-byte concatenation of its first two chunks with SHA-256 into the third, which may alias either.
    /// </param>
    /// <returns><see langword="true"/> if the accelerator now hashes merkle pairs; otherwise the SHA-256 accelerator keeps them.</returns>
    /// <remarks>
    /// Checks a pair whose hash is fixed rather than only checking that the call returns, once with the chunks
    /// side by side, as a merkle level's siblings are, and once apart, each time writing over the left chunk:
    /// a binding that resolved to the wrong routine or mishandled either layout would otherwise go unnoticed
    /// until a block produced a wrong root.
    /// </remarks>
    public static unsafe bool TryRegisterHashPairAccelerator(delegate*<in UInt256, in UInt256, out UInt256, void> hashPair)
    {
        Span<UInt256> chunks = stackalloc UInt256[3];
        chunks[0] = chunks[2] = ZeroHash(1);
        chunks[1] = ZeroHash(2);

        hashPair(in chunks[0], in chunks[1], out chunks[0]);
        hashPair(in chunks[2], in chunks[1], out chunks[2]);

        UInt256 expected = MemoryMarshal.Read<UInt256>(HashOfZeroHashes1And2);
        if (chunks[0] != expected || chunks[2] != expected)
            return false;

        _hashPairAccelerator = hashPair;
        return true;
    }

    /// <summary>SHA-256 of <c>ZeroHash(1) || ZeroHash(2)</c>.</summary>
    private static ReadOnlySpan<byte> HashOfZeroHashes1And2 =>
    [
        0x42, 0xb0, 0x52, 0x54, 0x1d, 0xce, 0x45, 0x55, 0x7d, 0x83, 0xd3, 0x46, 0x34, 0xa4, 0x5a, 0x56,
        0xd2, 0x16, 0xd4, 0x37, 0x5e, 0x5a, 0x95, 0x84, 0xf6, 0x44, 0x5c, 0xe4, 0xe6, 0x33, 0x24, 0xaf,
    ];
}
