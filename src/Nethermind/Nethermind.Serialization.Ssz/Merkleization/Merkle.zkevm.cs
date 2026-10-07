// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256 into <paramref name="parent"/>, which may alias either chunk.</summary>
    /// <remarks>
    /// The zkVM's SHA-256 compression when the ZisK guest switches on <see cref="ZiskSha256FFlag"/> or the OpenVM
    /// guest <see cref="OpenVmSha256Flag"/>, and the SHA-256 accelerator every zkVM provides otherwise.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashPair(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        if (!ZiskSha256FFlag.IsActive && !OpenVmSha256Flag.IsActive)
        {
            HashPairWithSha256(in left, in right, out parent);
            return;
        }

        HashPairWithSha256F(in left, in right, out parent);
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

    /// <summary>Hashes the 64-byte concatenation of two chunks with the zkVM's SHA-256 compression.</summary>
    /// <remarks>
    /// A 64-byte message is exactly one block, and the block after it is always the same padding, so the
    /// generic hash's alignment check, padding logic and result copies all drop out. Chunks that already lie
    /// side by side, as the siblings of a merkle level do, are absorbed where they are.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void HashPairWithSha256F(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        ReadOnlySpan<ulong> initialState = Sha256InitialState;
        Sha256State state;
        state.Word0 = initialState[0];
        state.Word1 = initialState[1];
        state.Word2 = initialState[2];
        state.Word3 = initialState[3];

        bool adjacent = Unsafe.AreSame(ref Unsafe.Add(ref Unsafe.AsRef(in left), 1), ref Unsafe.AsRef(in right));
        Sha256FParameters parameters;
        parameters.State = (ulong*)&state;
        fixed (UInt256* pair = &left)
        {
            // The precompile requires 8-byte aligned operands; the locals and the RVA padding block are.
            Sha256Block block;
            if (adjacent && ((nuint)pair & 7) == 0)
            {
                parameters.Input = (ulong*)pair;
            }
            else
            {
                block.Left = left;
                block.Right = right;
                parameters.Input = (ulong*)&block;
            }

            Compress(&parameters);
        }

        parameters.Input = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sha256PaddingOf64ByteMessage));
        Compress(&parameters);

        parent = new UInt256(ToDigestWord(state.Word0), ToDigestWord(state.Word1), ToDigestWord(state.Word2), ToDigestWord(state.Word3));
    }

    /// <summary>Compresses the block <paramref name="parameters"/> names into its state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Compress(Sha256FParameters* parameters)
    {
        if (OpenVmSha256Flag.IsActive)
            OpenVmSha256Compress(parameters->State, parameters->Input, parameters->State);
        else
            Sha256F(parameters);
    }

    /// <summary>Two state words of the precompile, native 32-bit words packed low first, as the digest's next eight bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ToDigestWord(ulong state) =>
        OpenVmSha256Flag.IsActive ? SwapBytesInHalves(state) : BitOperations.RotateRight(BinaryPrimitives.ReverseEndianness(state), 32);

    /// <summary>Reverses the bytes of each 32-bit half of <paramref name="x"/>.</summary>
    /// <remarks>
    /// <see cref="ToDigestWord"/> for a zkVM without <c>rev8</c>, where reversing the whole word is a long software
    /// sequence. The masked halves are disjoint, so they are added rather than or-ed: OpenVM's add rows are narrower.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SwapBytesInHalves(ulong x)
    {
        x = ((x & 0x00FF00FF00FF00FFUL) << 8) + ((x >> 8) & 0x00FF00FF00FF00FFUL);
        return ((x & 0x0000FFFF0000FFFFUL) << 16) + ((x >> 16) & 0x0000FFFF0000FFFFUL);
    }

    /// <summary>The SHA-256 initial hash value (FIPS 180-4 5.3.3) in the precompile's state layout.</summary>
    internal static ReadOnlySpan<ulong> Sha256InitialState =>
    [
        0xbb67ae85_6a09e667UL, 0xa54ff53a_3c6ef372UL, 0x9b05688c_510e527fUL, 0x5be0cd19_1f83d9abUL
    ];

    /// <summary>The block that pads a 64-byte message (FIPS 180-4 5.1.1): the 0x80 marker and the 512-bit length.</summary>
    internal static ReadOnlySpan<ulong> Sha256PaddingOf64ByteMessage =>
    [
        0x80UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0x0002_0000_0000_0000UL
    ];

    /// <summary>ZisK's SHA-256 compression precompile, called directly rather than through <see cref="Accelerators.Sha256F"/>.</summary>
    /// <remarks>
    /// One parameter block serves both compressions of a pair, so the second call rewrites only its input
    /// pointer, where the accelerator zeroes and refills a fresh block each call.
    /// <see cref="Sha256FParameters"/> mirrors the ZiskOS <c>syscall_sha256_f</c> ABI that Nethermind.Zkvm.Abstractions
    /// also encodes; nothing checks the two agree at compile time, so recheck it when that package is bumped.
    /// </remarks>
    [DllImport("__Internal", EntryPoint = "syscall_sha256_f", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void Sha256F(Sha256FParameters* parameters);

    /// <summary>OpenVM's SHA-256 compression of one block into <paramref name="output"/>, which may alias <paramref name="state"/>.</summary>
    /// <remarks>Takes the state in the layout of <see cref="Sha256F"/>, and every operand 8-byte aligned.</remarks>
    [DllImport("__Internal", EntryPoint = "zkvm_sha256_compress", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void OpenVmSha256Compress(ulong* state, ulong* input, ulong* output);

    private unsafe struct Sha256FParameters
    {
        public ulong* State;
        public ulong* Input;
    }

    private struct Sha256State
    {
        public ulong Word0;
        public ulong Word1;
        public ulong Word2;
        public ulong Word3;
    }

    private struct Sha256Block
    {
        public UInt256 Left;
        public UInt256 Right;
    }
}
