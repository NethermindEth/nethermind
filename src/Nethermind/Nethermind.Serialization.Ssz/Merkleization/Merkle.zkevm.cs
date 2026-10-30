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
    /// guest <see cref="OpenVmSha256Flag"/>, SP1's extend and compress precompiles when the SP1 guest switches on
    /// <see cref="Sp1Sha256Flag"/>, and the SHA-256 accelerator every zkVM provides otherwise.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashPair(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        if (ZiskSha256FFlag.IsActive || OpenVmSha256Flag.IsActive)
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

    /// <summary>
    /// Hashes the sibling pairs of <paramref name="leaves"/> into the first nodes of <paramref name="nodes"/>, then reduces
    /// the <paramref name="nodeCount"/> nodes level by level up to <paramref name="depth"/>, leaving the root in the first.
    /// </summary>
    /// <returns>
    /// <c>false</c>, having hashed nothing, unless the guest runs on SP1 or ZisK's SHA-256 compression can read the
    /// leaves in place.
    /// </returns>
    /// <remarks>
    /// One loop owns the state and parameter block for the whole tree, so a pair costs its two compressions, the state
    /// reset and the digest, without the per-pair call, setup and spills of <see cref="HashNodes"/>. A parent never
    /// overwrites a node the level has yet to read, as parent <c>i</c> lies at or before child <c>2i</c>; and a lone last
    /// node pairs with the zero hash written after it, so <paramref name="nodes"/> must hold one node more than an odd
    /// <paramref name="nodeCount"/>.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe bool TryHashTree(ReadOnlySpan<UInt256> leaves, Span<UInt256> nodes, int nodeCount, int depth)
    {
        if (Sp1Sha256Flag.IsActive)
        {
            HashTreeWithSp1Sha256(leaves, nodes, nodeCount, depth);
            return true;
        }

        if (!ZiskSha256FFlag.IsActive)
            return false;

        // Nothing below allocates and the guest is single-threaded, so no GC can move the buffers: pinned locals
        // would stay on the stack and spill around every compression.
        UInt256* firstLeaf = (UInt256*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(leaves));
        UInt256* firstNode = (UInt256*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(nodes));

        // The precompile requires 8-byte aligned operands; the node buffer is.
        if (((nuint)firstLeaf & 7) != 0)
            return false;

        ref readonly Sha256State initialState = ref Unsafe.As<ulong, Sha256State>(ref MemoryMarshal.GetReference(Sha256InitialState));
        ulong* padding = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sha256PaddingOf64ByteMessage));
        Sha256State state;

        UInt256* children = firstLeaf;
        int pairs = leaves.Length / 2;
        for (int level = 0; ;)
        {
            UInt256* parent = firstNode;
            for (UInt256* pair = children, end = children + 2 * pairs; pair != end; pair += 2, parent++)
            {
                if (IsZeroSubtree(in pair[0], level) && IsZeroSubtree(in pair[1], level))
                {
                    *parent = ZeroHash(level + 1);
                    continue;
                }

                state = initialState;
                Sha256F((ulong*)&state, (ulong*)pair);
                Sha256F((ulong*)&state, padding);
                *parent = new UInt256(ToDigestWord(state.Word0), ToDigestWord(state.Word1), ToDigestWord(state.Word2), ToDigestWord(state.Word3));
            }

            if (++level >= depth)
                return true;

            if (nodeCount % 2 == 1)
                nodes[nodeCount] = ZeroHash(level);

            children = firstNode;
            pairs = (nodeCount + 1) / 2;
            nodeCount = pairs;
        }
    }

    /// <summary>Hashes the 64-byte concatenation of two chunks with the zkVM's SHA-256 compression.</summary>
    /// <remarks>
    /// A 64-byte message is exactly one block, and the block after it is always the same padding, so the
    /// generic hash's alignment check, padding logic and result copies all drop out. Chunks that already lie
    /// side by side, as the siblings of a merkle level do, are absorbed where they are.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void HashPairWithSha256F(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        Sha256State state = Unsafe.As<ulong, Sha256State>(ref MemoryMarshal.GetReference(Sha256InitialState));

        bool adjacent = Unsafe.AreSame(ref Unsafe.Add(ref Unsafe.AsRef(in left), 1), ref Unsafe.AsRef(in right));
        fixed (UInt256* pair = &left)
        {
            // The precompile requires 8-byte aligned operands; the locals and the RVA padding block are.
            NodePair block;
            ulong* input;
            if (adjacent && ((nuint)pair & 7) == 0)
            {
                input = (ulong*)pair;
            }
            else
            {
                block.Left = left;
                block.Right = right;
                input = (ulong*)&block;
            }

            Compress((ulong*)&state, input);
        }

        Compress((ulong*)&state, (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sha256PaddingOf64ByteMessage)));

        parent = new UInt256(ToDigestWord(state.Word0), ToDigestWord(state.Word1), ToDigestWord(state.Word2), ToDigestWord(state.Word3));
    }

    /// <summary>Compresses the block at <paramref name="input"/> into <paramref name="state"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Compress(ulong* state, ulong* input)
    {
        if (OpenVmSha256Flag.IsActive)
            Accelerators.OpenVMSha256Compress(state, input, state);
        else
            Sha256F(state, input);
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
    /// Since ZisK v1.4.0-alpha the precompile takes the state and input addresses directly, with no parameter
    /// block; the new state overwrites <paramref name="state"/>. This mirrors the ZiskOS <c>syscall_sha256_f</c>
    /// ABI and nothing checks the two agree at compile time, so recheck it when Nethermind.ZiskOS.Runtime is bumped.
    /// </remarks>
    [DllImport("__Internal", EntryPoint = "syscall_sha256_f", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void Sha256F(ulong* state, ulong* input);

    private struct Sha256State
    {
        public ulong Word0;
        public ulong Word1;
        public ulong Word2;
        public ulong Word3;
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
        CompressSp1Sha256Block(schedule, state);
        parent = ReadSp1Sha256Digest(state);
    }

    /// <summary>Reduces a tree as <see cref="TryHashTree"/> does, with SP1's SHA-256 extend and compress precompiles.</summary>
    /// <remarks>
    /// The nodes above the leaves stay in the precompiles' word order, as <see cref="PackSp1Sha256State"/> leaves a
    /// digest, so only the leaves and the root swap their bytes. A lone last node pairs with its level's zero hash from
    /// <see cref="Sp1Sha256ZeroHashWords"/>, which is in that order already. Above its data, a list's tree is only such
    /// pairs up to the list's limit: about twenty levels for each transaction of a payload.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void HashTreeWithSp1Sha256(ReadOnlySpan<UInt256> leaves, Span<UInt256> nodes, int nodeCount, int depth)
    {
        ulong* schedule = stackalloc ulong[Sp1Sha256ScheduleLength];
        ulong* state = stackalloc ulong[Sp1Sha256StateLength];

        // Nothing below allocates and the guest is single-threaded, so no GC can move the buffers.
        UInt256* leaf = (UInt256*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(leaves));
        UInt256* node = (UInt256*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(nodes));

        int pairs = leaves.Length / 2;
        for (int i = 0; i < pairs; i++, leaf += 2)
        {
            LoadSp1Sha256Block(in leaf[0], in leaf[1], schedule, state);
            CompressSp1Sha256Block(schedule, state);
            node[i] = PackSp1Sha256State(state);
        }

        // The caller hashes a lone last pair of leaves itself, into byte order.
        if (nodeCount > pairs)
            node[pairs] = SwapBytesInWords(in node[pairs]);

        int level = 1;
        for (; level < depth && nodeCount > 1; level++)
        {
            int parentCount = nodeCount / 2;
            for (int i = 0; i < parentCount; i++)
            {
                LoadSp1Sha256WordBlock(in node[2 * i], in node[2 * i + 1], schedule, state);
                CompressSp1Sha256Block(schedule, state);
                node[i] = PackSp1Sha256State(state);
            }

            if (nodeCount % 2 == 1)
            {
                LoadSp1Sha256WordBlock(in node[nodeCount - 1], in Sp1Sha256ZeroHashWords(level), schedule, state);
                CompressSp1Sha256Block(schedule, state);
                node[parentCount++] = PackSp1Sha256State(state);
            }

            nodeCount = parentCount;
        }

        // The root left pairs with the zero hash of every level up to the limit, so each digest goes from the state
        // slots straight into the message of the next block.
        SplitSp1Sha256Words(node->u0, schedule);
        SplitSp1Sha256Words(node->u1, schedule + 2);
        SplitSp1Sha256Words(node->u2, schedule + 4);
        SplitSp1Sha256Words(node->u3, schedule + 6);
        for (; level < depth; level++)
        {
            ref readonly UInt256 zeroHash = ref Sp1Sha256ZeroHashWords(level);
            SplitSp1Sha256Words(zeroHash.u0, schedule + 8);
            SplitSp1Sha256Words(zeroHash.u1, schedule + 10);
            SplitSp1Sha256Words(zeroHash.u2, schedule + 12);
            SplitSp1Sha256Words(zeroHash.u3, schedule + 14);
            LoadSp1Sha256InitialState(state);
            CompressSp1Sha256Block(schedule, state);
            for (int i = 0; i < Sp1Sha256StateLength; i++)
                schedule[i] = state[i];
        }

        *node = ReadSp1Sha256Digest(schedule);
    }

    /// <summary>Hashes the block in the first sixteen slots of <paramref name="schedule"/> and the padding block after it into <paramref name="state"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CompressSp1Sha256Block(ulong* schedule, ulong* state)
    {
        Accelerators.Sha256Extend(schedule);
        Accelerators.Sp1Sha256Compress(schedule, state);
        Accelerators.Sp1Sha256Compress((ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Sp1Sha256PaddingSchedule)), state);
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
        LoadSp1Sha256InitialState(state);
    }

    /// <summary>As <see cref="LoadSp1Sha256Block"/>, for two nodes in the precompiles' word order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void LoadSp1Sha256WordBlock(in UInt256 left, in UInt256 right, ulong* schedule, ulong* state)
    {
        SplitSp1Sha256Words(left.u0, schedule);
        SplitSp1Sha256Words(left.u1, schedule + 2);
        SplitSp1Sha256Words(left.u2, schedule + 4);
        SplitSp1Sha256Words(left.u3, schedule + 6);
        SplitSp1Sha256Words(right.u0, schedule + 8);
        SplitSp1Sha256Words(right.u1, schedule + 10);
        SplitSp1Sha256Words(right.u2, schedule + 12);
        SplitSp1Sha256Words(right.u3, schedule + 14);
        LoadSp1Sha256InitialState(state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void LoadSp1Sha256InitialState(ulong* state)
    {
        // The SHA-256 initial hash value (FIPS 180-4 5.3.3) as immediates: a copy from a table costs a loop and an
        // alignment test per word on SP1.
        state[0] = 0x6a09e667UL;
        state[1] = 0xbb67ae85UL;
        state[2] = 0x3c6ef372UL;
        state[3] = 0xa54ff53aUL;
        state[4] = 0x510e527fUL;
        state[5] = 0x9b05688cUL;
        state[6] = 0x1f83d9abUL;
        state[7] = 0x5be0cd19UL;
    }

    /// <summary>Reads the digest out of SP1's SHA-256 state slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe UInt256 ReadSp1Sha256Digest(ulong* state) => new(
        SwapBytesInWords(state[0] | (state[1] << 32)),
        SwapBytesInWords(state[2] | (state[3] << 32)),
        SwapBytesInWords(state[4] | (state[5] << 32)),
        SwapBytesInWords(state[6] | (state[7] << 32)));

    /// <summary>Reads SP1's SHA-256 state slots as a node in the precompiles' word order: the digest words packed two to a limb, low first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe UInt256 PackSp1Sha256State(ulong* state) => new(
        state[0] | (state[1] << 32),
        state[2] | (state[3] << 32),
        state[4] | (state[5] << 32),
        state[6] | (state[7] << 32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void LoadSp1Sha256Words(ulong bytes, ulong* slots) => SplitSp1Sha256Words(SwapBytesInWords(bytes), slots);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void SplitSp1Sha256Words(ulong words, ulong* slots)
    {
        slots[0] = (uint)words;
        slots[1] = words >> 32;
    }

    /// <summary>Converts a node between byte order and the precompiles' word order, either way.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static UInt256 SwapBytesInWords(in UInt256 node) =>
        new(SwapBytesInWords(node.u0), SwapBytesInWords(node.u1), SwapBytesInWords(node.u2), SwapBytesInWords(node.u3));

    /// <summary>Reverses the bytes of each 32-bit half of <paramref name="x"/> in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SwapBytesInWords(ulong x)
    {
        x = ((x & 0x00FF00FF00FF00FFUL) << 8) | ((x >> 8) & 0x00FF00FF00FF00FFUL);
        return ((x & 0x0000FFFF0000FFFFUL) << 16) | ((x >> 16) & 0x0000FFFF0000FFFFUL);
    }

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

    /// <summary>The zero-subtree hash for <paramref name="level"/> in the precompiles' word order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref readonly UInt256 Sp1Sha256ZeroHashWords(int level) =>
        ref Unsafe.Add(ref Unsafe.As<ulong, UInt256>(ref MemoryMarshal.GetReference(Sp1Sha256ZeroHashWordData)), level);

    /// <summary><see cref="ZeroHashData"/> with the bytes of each 32-bit word swapped, four limbs to a level.</summary>
    private static ReadOnlySpan<ulong> Sp1Sha256ZeroHashWordData =>
    [
        0x00000000_00000000UL, 0x00000000_00000000UL, 0x00000000_00000000UL, 0x00000000_00000000UL,
        0xd16a2030_f5a5fd42UL, 0xd309979b_2798ef6eUL, 0x20d9f0e8_43003d23UL, 0x2759fb4b_ea9831a9UL,
        0x00fdd4c1_db56114eUL, 0xf35ac9a8_f85c892bUL, 0xb1ebd0a9_9289aaecUL, 0x748b5d71_6cde606aUL,
        0xf07fc56a_c78009fdUL, 0x0658a353_11f12237UL, 0x63e44c4b_aaa542edUL, 0x105ab33c_c15ff4cdUL,
        0x7f2dd165_536d9883UL, 0xe9148595_a55d5eeaUL, 0x246df256_4472d56fUL, 0x352a123c_bf3cae19UL,
        0xaa15429f_9efde052UL, 0xd0b1d7c6_ae05bad4UL, 0xd7a1854a_4da64d03UL, 0x430c0d30_588c2cb8UL,
        0xd400a875_d88ddfeeUL, 0x42c1497e_5596b219UL, 0x6118290f_114c302eUL, 0x76041fa1_91e67729UL,
        0xa57e35f6_87eb0ddbUL, 0x02a4af59_d2866738UL, 0xc7cf4c64_75e22506UL, 0x11527f2c_bb6be5eeUL,
        0xfd5fc54a_26846476UL, 0x67c95144_5d433851UL, 0x3cc85bb9_f2643f53UL, 0x8d7db193_d16b782fUL,
        0x2d252405_506d8658UL, 0x92cad2bf_b8400187UL, 0x5aa5f887_1259f1efUL, 0x094f51e1_e13cb2f0UL,
        0xe659772f_ffff0ad7UL, 0xc815efc4_9534c195UL, 0xdaed4404_014ef1e1UL, 0x1192e92b_c06385d1UL,
        0xdb05441c_6cf04127UL, 0x52be8528_d833107aUL, 0x17e6a02a_68890e43UL, 0x75964220_b47683aaUL,
        0x5f140027_b7d05f87UL, 0x247bbb84_ef5118a2UL, 0x11236230_ce8f2f0fUL, 0x0c329f5f_85daf796UL,
        0xbbdb6be9_df6af5f5UL, 0xe4bf8073_ef8aa618UL, 0x1e29676f_96086717UL, 0x6a08a85e_8b284deaUL,
        0x5e182e3c_b58d900fUL, 0x9ea16c77_50ef7496UL, 0x7cc23523_26c54975UL, 0xa7293784_c369587dUL,
        0xffcfb034_d49a7502UL, 0x688500ca_0b1d7885UL, 0xf96b62df_308161a7UL, 0xfcc8f2bb_9d083b71UL,
        0x9256c0d3_8fe6b168UL, 0xbe2027a2_85f42f5bUL, 0x10ba97c1_2c1996e1UL, 0x8de92beb_71d3e594UL,
        0x9ebade85_8d0d63c3UL, 0x9c3876fb_09e0ae3cUL, 0x18f905ec_5fa112beUL, 0x057603ab_acfecb92UL,
        0xe541cad4_95eec8b2UL, 0x85f2e046_e91de383UL, 0x6c2382cb_619f5449UL, 0x8c26f5a4_6cacd5b9UL,
        0x917775b6_f893e908UL, 0x4dbbe3a1_2bff2329UL, 0xc35b4801_cd8e6cc1UL, 0x6f81f17f_887b646aUL,
        0x92e31333_cddba7b5UL, 0xfac7431a_93c16194UL, 0xed711db2_bf2f5485UL, 0x9e08ebaa_82183c81UL,
        0xaf8caa08_8a8d7fe3UL, 0x32001457_5a7639a8UL, 0x8061142a_dfb9128aUL, 0xff23ff9c_d0335629UL,
        0xd7a51a6f_feb3c337UL, 0x4c52e1c9_bf00b9e3UL, 0xd4e7a0bf_195c969bUL, 0xed9c1167_d51d5c5bUL,
        0x3cc32edf_e71f0aa8UL, 0x3e0174ca_befa9f4dUL, 0x9f3a09f6_85182eecUL, 0x77a510d7_a6c0df63UL,
        0x0a50bb6a_31206fa8UL, 0x58f16212_be290850UL, 0xc8f049fe_212a60eeUL, 0xe0a84bc0_cb92d8c8UL,
        0xcbeddde9_21352bfeUL, 0x4c3dac0a_93839f61UL, 0xf9b412b1_3ee37543UL, 0x8e23b544_6199dc15UL,
        0x24bb6d7c_619e3127UL, 0xe791d764_3153ed9dUL, 0xaf13c58b_a366b389UL, 0x81a46765_f8a8d904UL,
        0x26825062_7cdd2986UL, 0x85c58c61_8d0c10e3UL, 0x5191bcc0_91e6fbe0UL, 0xea72c1c4_4f133f2cUL,
        0x7ba8cac5_848930bdUL, 0x13fb2788_46610721UL, 0x587f9139_69e07bb8UL, 0x017bcbe1_2933374dUL,
        0x22b28cc1_8869ff2cUL, 0x32928033_0510d985UL, 0xe80495e8_28be4fb0UL, 0x5b889636_bb8d271fUL,
        0x9f1b850f_b5fe28e7UL, 0xe9b6a1e7_8658246cUL, 0xb7143e8f_b49fc06dUL, 0xc5523a5c_e0b4f2b0UL,
        0x70af28d0_985e929fUL, 0x808f977f_bdd1a90aUL, 0x8c489e98_597c7c77UL, 0xd31ac0f7_d3bd8910UL,
        0xe6e4e1bd_c6f67e02UL, 0x098953f3_efb994c6UL, 0x6ca20a47_4636ba2bUL, 0x886722ff_21d2b26aUL,
        0xf1cf48b4_1c9a7e5fUL, 0xf4e4a100_ad1582d3UL, 0xc5a2b713_4f3b20d8UL, 0xd933ebc5_87a4254aUL,
        0x29646b6f_2f075ae2UL, 0xe372cf29_6aed19a5UL, 0xb893ff59_5081401eUL, 0x0c0d3e7d_9b3f9accUL,
        0xb5961207_328921deUL, 0x61592107_6801e8cdUL, 0xb846595c_b5c67c79UL, 0x5b46362c_c6320c39UL,
        0xb236ad24_bfb909fdUL, 0x3810a074_11b4e488UL, 0x89986c3f_b8404646UL, 0x7e17c327_8a809182UL,
        0x87ba3ba4_55d8fb36UL, 0xf5a1f89b_9f342c77UL, 0x446e1a46_ec83d811UL, 0x640b6a74_7139213dUL,
        0x8e7e1039_f7210d4fUL, 0xefa20755_790e7bf4UL, 0x1dd4b95d_5a10a6dbUL, 0x8b88fe76_a313aaa8UL,
        0xcbc645ff_ad21b516UL, 0x1c8aef8c_e34ab5deUL, 0xb51e8e14_d4e7f8d2UL, 0x3cda206f_56adc756UL,
        0xcc4237b7_6bfe8d2bUL, 0x8ef45533_4a504705UL, 0xcb63bfbb_9ecd7360UL, 0x6430ba04_8ee5448eUL,
        0x181740dc_a7f23ce9UL, 0x82654fee_220c8147UL, 0xec9222c4_6aceb9f1UL, 0xb1680837_e2467d0aUL,
        0x89590a2c_aef9476cUL, 0x4f4967c7_8cc9b3b7UL, 0x66a44bac_57c49d98UL, 0x675ddfa2_f21fa2edUL,
        0x82f6a9e4_9a42bcadUL, 0xead319f2_1284d808UL, 0x9d680f0e_9f3b0820UL, 0xd071e205_2ce71510UL,
        0x4a67b9cf_d1a66d35UL, 0xe5f97792_179571d8UL, 0xec441968_716e8dd4UL, 0xb74f8bac_39a3f7c6UL,
        0xf2f89509_fafa3025UL, 0xfba0cd92_c2c71c74UL, 0x0780fb54_858ef49bUL, 0x9bfcb346_79746c8aUL,
        0xe7f6705a_3334a7c1UL, 0x94964501_a6011a6aUL, 0x0ca9abd6_6db4acdeUL, 0x66423056_6dc79d82UL,
        0x664faef7_0796fd75UL, 0xd7271e2b_44ee4e52UL, 0xed6f9b74_bb769f91UL, 0x6606852c_d8b694f5UL,
        0x417fe854_7ba3ae4aUL, 0x9f4adcd7_5b142bc8UL, 0xbab7750b_ae13941cUL, 0x6d16be64_83e9f0a6UL,
        0x4aa52039_788fafccUL, 0x95f8b12c_9adbaed1UL, 0x0168e50a_4eb31ec1UL, 0xaea516dc_abc659a6UL,
        0x7160e68b_e833d7a6UL, 0x53077df2_f4c9044aUL, 0xf36f4949_727ad00cUL, 0x12140cbb_c7b681a9UL,
        0x95dc6714_309eabf0UL, 0xbba5affa_f9f4d864UL, 0xf5e3565b_e0b35ae2UL, 0x12767701_cc3a47b2UL,
        0xfa288665_226a8ebeUL, 0x73335efb_a644a502UL, 0x241b5b72_b610510fUL, 0x59a69a5d_0c8a368dUL,
        0x54258276_41abfd99UL, 0xaf0c4f33_25938131UL, 0x8c222c21_fe0bd468UL, 0x9caa03f8_fa9da8e8UL,
        0xf50fa1a6_442c642eUL, 0x05c77c5c_67a6e6d1UL, 0xaa2570cf_c3fec8d7UL, 0x03c38069_1a3077b5UL,
        0x9b42d96c_a0a08dfcUL, 0x127b8ae1_2de19b6dUL, 0x5ad0dce4_36ddcf3eUL, 0xf61f6a74_22c45a56UL,
        0xaf096dbe_7d348382UL, 0xbb39b2a2_0bf086c7UL, 0x21ab0c73_c0bc36b6UL, 0x31d81740_8e9885d7UL,
        0x1d191269_3ab13475UL, 0x4eaa8b43_026c8699UL, 0xf6d0e773_a83b4ad1UL, 0x4afbc8f6_81c4e297UL,
        0x1db2d23e_9a745261UL, 0xbb88958e_ae26f9bdUL, 0xfe987be9_f44c64d0UL, 0x38f50f6c_f726adf9UL,
        0x6037bfe4_725c7f81UL, 0xa35ac47e_52cd1e7bUL, 0x2b27aeca_dcb49a9aUL, 0xcb7ded1f_70dce483UL,
        0x1fb28b62_2cea1af5UL, 0x8ac9fef4_887c3999UL, 0x07e071ba_dfdeda1fUL, 0xfd06cbc3_558a173aUL,
        0x8b6c551d_ff1d59f9UL, 0x057d5c8b_95089357UL, 0x9e9df0b1_e2640227UL, 0x2bf3927f_df1a10b7UL,
        0x7c99dd21_2f8a181fUL, 0xe296a960_5a7529bfUL, 0x7186d21a_3a144673UL, 0x59e1fd21_eb8bc7aeUL,
        0xb1145f39_ecc502c9UL, 0x3842446f_50cb7d3eUL, 0x1df537ce_81a4f0dfUL, 0xea984bd9_e139ef64UL
    ];
}
