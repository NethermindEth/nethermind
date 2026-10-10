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
    /// ZisK's SHA-256 compression precompile when the ZisK guest switches on <see cref="ZiskSha256FFlag"/>, SP1's
    /// extend and compress precompiles when the SP1 guest switches on <see cref="Sp1Sha256Flag"/>, and the SHA-256
    /// accelerator every zkVM provides otherwise.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashPair(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        if (ZiskSha256FFlag.IsActive)
            HashPairWithSha256F(in left, in right, out parent);
        else if (Sp1Sha256Flag.IsActive)
            HashPairWithSp1Sha256(in left, in right, out parent);
        else
            HashPairWithSha256(in left, in right, out parent);
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
    /// Padding never forms a pair of zero subtrees in the level loop, so only zero chunks in the data do.
    /// ZisK looks such pairs up as the host does: its compressions fill Sha256f instances of fixed capacity,
    /// so the two each pair saves can drop a whole instance from the proof. The other zkVMs hash them, as
    /// testing every pair costs them more than the rare hash it saves.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashNodes(in UInt256 left, in UInt256 right, int level, out UInt256 parent)
    {
        if (ZiskSha256FFlag.IsActive && IsZeroSubtree(in left, level) && IsZeroSubtree(in right, level))
        {
            parent = ZeroHash(level + 1);
            return;
        }

        HashPair(in left, in right, out parent);
    }

    /// <summary>Whether <paramref name="node"/> is the root of an all-zero subtree at <paramref name="level"/>.</summary>
    /// <remarks>Compares in place, a limb at a time, so the usual node rejects on its first limb.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsZeroSubtree(in UInt256 node, int level)
    {
        ref readonly UInt256 zeroHash = ref Unsafe.Add(ref Unsafe.As<byte, UInt256>(ref MemoryMarshal.GetReference(ZeroHashData)), level);
        return node.u0 == zeroHash.u0 && node.u1 == zeroHash.u1 && node.u2 == zeroHash.u2 && node.u3 == zeroHash.u3;
    }

    /// <summary>Hashes the 64-byte concatenation of two chunks with ZisK's SHA-256 compression precompile.</summary>
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

            Sha256F(&parameters);
        }

        parameters.Input = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sha256PaddingOf64ByteMessage));
        Sha256F(&parameters);

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

    /// <summary>ZisK's SHA-256 compression precompile, called directly rather than through <see cref="Accelerators.Sha256F"/>.</summary>
    /// <remarks>
    /// One parameter block serves both compressions of a pair, so the second call rewrites only its input
    /// pointer, where the accelerator zeroes and refills a fresh block each call.
    /// <see cref="Sha256FParameters"/> mirrors the ZiskOS <c>syscall_sha256_f</c> ABI that Nethermind.Zkvm.Abstractions
    /// also encodes; nothing checks the two agree at compile time, so recheck it when that package is bumped.
    /// </remarks>
    [DllImport("__Internal", EntryPoint = "syscall_sha256_f", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void Sha256F(Sha256FParameters* parameters);

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

    internal const int Sp1Sha256ScheduleLength = 64;
    internal const int Sp1Sha256StateLength = 8;

    /// <summary>Hashes the 64-byte concatenation of two chunks with SP1's SHA-256 extend and compress precompiles.</summary>
    /// <remarks>
    /// As in <see cref="HashPairWithSha256F"/>, the padding block is a constant, and so is its extended schedule:
    /// a pair costs one extension and two compressions, where SP1's generic hash also zeroes and refills a block
    /// buffer around them.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void HashPairWithSp1Sha256(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        ulong* schedule = stackalloc ulong[Sp1Sha256ScheduleLength];
        ulong* state = stackalloc ulong[Sp1Sha256StateLength];
        LoadSp1Sha256Block(in left, in right, schedule, state);

        Sp1Sha256Extend(schedule);
        Sp1Sha256Compress(schedule, state);
        Sp1Sha256Compress((ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sp1Sha256PaddingSchedule)), state);

        parent = ReadSp1Sha256Digest(state);
    }

    /// <summary>
    /// Writes the message words of the block <paramref name="left"/> || <paramref name="right"/> into the first
    /// sixteen slots of <paramref name="schedule"/>, and the SHA-256 initial hash value into <paramref name="state"/>.
    /// </summary>
    /// <remarks>SP1's precompiles hold every 32-bit word, big-endian as SHA-256 reads it, in the low half of a 64-bit slot.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void LoadSp1Sha256Block(in UInt256 left, in UInt256 right, ulong* schedule, ulong* state)
    {
        LoadSp1Sha256Words(left.u0, schedule);
        LoadSp1Sha256Words(left.u1, schedule + 2);
        LoadSp1Sha256Words(left.u2, schedule + 4);
        LoadSp1Sha256Words(left.u3, schedule + 6);
        LoadSp1Sha256Words(right.u0, schedule + 8);
        LoadSp1Sha256Words(right.u1, schedule + 10);
        LoadSp1Sha256Words(right.u2, schedule + 12);
        LoadSp1Sha256Words(right.u3, schedule + 14);

        ReadOnlySpan<ulong> initialState = Sp1Sha256InitialState;
        for (int i = 0; i < Sp1Sha256StateLength; i++)
            state[i] = initialState[i];
    }

    /// <summary>Reads the digest out of SP1's SHA-256 state slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe UInt256 ReadSp1Sha256Digest(ulong* state) => new(
        SwapBytesInWords(state[0] | (state[1] << 32)),
        SwapBytesInWords(state[2] | (state[3] << 32)),
        SwapBytesInWords(state[4] | (state[5] << 32)),
        SwapBytesInWords(state[6] | (state[7] << 32)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void LoadSp1Sha256Words(ulong bytes, ulong* slots)
    {
        ulong words = SwapBytesInWords(bytes);
        slots[0] = (uint)words;
        slots[1] = words >> 32;
    }

    /// <summary>Reverses the bytes of each 32-bit half of <paramref name="x"/> in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SwapBytesInWords(ulong x)
    {
        x = ((x & 0x00FF00FF00FF00FFUL) << 8) | ((x >> 8) & 0x00FF00FF00FF00FFUL);
        return ((x & 0x0000FFFF0000FFFFUL) << 16) | ((x >> 16) & 0x0000FFFF0000FFFFUL);
    }

    /// <summary>The SHA-256 initial hash value (FIPS 180-4 5.3.3), one word per slot.</summary>
    private static ReadOnlySpan<ulong> Sp1Sha256InitialState =>
    [
        0x6a09e667UL, 0xbb67ae85UL, 0x3c6ef372UL, 0xa54ff53aUL, 0x510e527fUL, 0x9b05688cUL, 0x1f83d9abUL, 0x5be0cd19UL
    ];

    /// <summary>The extended message schedule of the block that pads a 64-byte message, one word per slot.</summary>
    internal static ReadOnlySpan<ulong> Sp1Sha256PaddingSchedule =>
    [
        0x80000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL,
        0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000000UL, 0x00000200UL,
        0x80000000UL, 0x01400000UL, 0x00205000UL, 0x00005088UL, 0x22000800UL, 0x22550014UL, 0x05089742UL, 0xa0000020UL,
        0x5a880000UL, 0x005c9400UL, 0x0016d49dUL, 0xfa801f00UL, 0xd33225d0UL, 0x11675959UL, 0xf6e6bfdaUL, 0xb30c1549UL,
        0x08b2b050UL, 0x9d7c4c27UL, 0x0ce2a393UL, 0x88e6e1eaUL, 0xa52b4335UL, 0x67a16f49UL, 0xd732016fUL, 0x4eeb2e91UL,
        0x5dbf55e5UL, 0x8eee2335UL, 0xe2bc5ec2UL, 0xa83f4394UL, 0x45ad78f7UL, 0x36f3d0cdUL, 0xd99c05e8UL, 0xb0511dc7UL,
        0x69bc7ac4UL, 0xbd11375bUL, 0xe3ba71e5UL, 0x3b209ff2UL, 0x18feee17UL, 0xe25ad9e7UL, 0x13375046UL, 0x0515089dUL,
        0x4f0d0f04UL, 0x2627484eUL, 0x310128d2UL, 0xc668b434UL, 0x420841ccUL, 0x62d311b8UL, 0xe59ba771UL, 0x85a7a484UL
    ];

    /// <summary>SP1's SHA-256 message schedule extension: fills slots 16 to 63 from the first sixteen.</summary>
    [DllImport("__Internal", EntryPoint = "zkvm_sha256_extend", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void Sp1Sha256Extend(ulong* schedule);

    /// <summary>SP1's SHA-256 compression of one extended schedule into <paramref name="state"/>.</summary>
    [DllImport("__Internal", EntryPoint = "zkvm_sha256_compress", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void Sp1Sha256Compress(ulong* schedule, ulong* state);
}
