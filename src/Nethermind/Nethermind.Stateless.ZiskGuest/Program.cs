// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.Guest;

partial class Program
{
    private static partial void WriteOutput(ReadOnlySpan<byte> output)
    {
        // For debugging purposes
        IO.PrintLine(Convert.ToHexStringLower(output));

        IO.WriteOutput(output);
    }

    /// <summary>Hands merkleization's pair hashing to ZisK's SHA-256 compression precompile before the first block runs.</summary>
    /// <remarks>
    /// A module initializer because <c>Main</c> is shared by every guest. Bound here rather than in
    /// Nethermind.Serialization.Ssz: the SP1 and OpenVM guests link that assembly too and have no such symbol.
    /// </remarks>
    [ModuleInitializer]
    internal static unsafe void UseSha256F() => Merkle.TryRegisterHashPairAccelerator(&HashPair);

    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256 into <paramref name="parent"/>, which may alias either chunk.</summary>
    /// <remarks>
    /// Drives the SHA-256 compression precompile directly. A 64-byte message is exactly one block, and the
    /// block after it is always the same padding, so the generic hash's alignment check, padding logic and
    /// result copies all drop out, along with its transitioning P/Invoke. Chunks that already lie side by
    /// side, as the siblings of a merkle level do, are absorbed where they are.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void HashPair(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        ReadOnlySpan<ulong> initialState = Sha256InitialState;
        Sha256State state;
        state.Word0 = initialState[0];
        state.Word1 = initialState[1];
        state.Word2 = initialState[2];
        state.Word3 = initialState[3];

        Sha256Parameters parameters;
        parameters.State = (ulong*)&state;

        bool adjacent = Unsafe.AreSame(ref Unsafe.Add(ref Unsafe.AsRef(in left), 1), ref Unsafe.AsRef(in right));
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

            syscall_sha256_f(&parameters);
        }

        parameters.Input = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sha256PaddingOf64ByteMessage));
        syscall_sha256_f(&parameters);

        parent = new UInt256(ToDigestWord(state.Word0), ToDigestWord(state.Word1), ToDigestWord(state.Word2), ToDigestWord(state.Word3));
    }

    /// <summary>Two state words of the precompile, native 32-bit words packed low first, as the digest's next eight bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ToDigestWord(ulong state) => BitOperations.RotateRight(BinaryPrimitives.ReverseEndianness(state), 32);

    /// <summary>The SHA-256 initial hash value (FIPS 180-4 5.3.3) in the precompile's state layout.</summary>
    private static ReadOnlySpan<ulong> Sha256InitialState =>
    [
        0xbb67ae85_6a09e667UL, 0xa54ff53a_3c6ef372UL, 0x9b05688c_510e527fUL, 0x5be0cd19_1f83d9abUL
    ];

    /// <summary>The block that pads a 64-byte message (FIPS 180-4 5.1.1): the 0x80 marker and the 512-bit length.</summary>
    private static ReadOnlySpan<ulong> Sha256PaddingOf64ByteMessage =>
    [
        0x80UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0x0002_0000_0000_0000UL
    ];

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

    private unsafe struct Sha256Parameters
    {
        public ulong* State;
        public ulong* Input;
    }

    /// <summary>The SHA-256 compression precompile: absorbs the 64-byte block at <c>Input</c> into <c>State</c>.</summary>
    /// <remarks>
    /// Suppresses the GC transition as the entry point is a single CSR write, which can neither block nor
    /// call back. A <c>DllImport</c> rather than a <c>LibraryImport</c> because bflat compiles this file
    /// without source generators.
    /// </remarks>
    [DllImport("__Internal")]
    [SuppressGCTransition]
    private static extern unsafe void syscall_sha256_f(Sha256Parameters* parameters);
}
