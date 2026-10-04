// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Crypto;

/// <summary>
/// The guest keccak's retained full-branch prefixes in <c>KeccakHash.zkevm.cs</c>: which retained sponge state a
/// full branch resumes from.
/// </summary>
/// <remarks>
/// Driven through <see cref="KeccakHash.Retain"/> and <see cref="KeccakHash.FindRetainedState"/> with stand-in
/// states, because in a ZK_EVM build the permutation is a zkVM precompile that throws outside the guest. What the
/// lookup owes its caller is the state stored for the input sharing the matched blocks, whatever that state is.
/// </remarks>
// Every case replaces the process-wide retained set, hence NonParallelizable.
[NonParallelizable]
public class KeccakRetainedPrefixTests
{
    private const int Length = KeccakHash.Hash532InputLength;
    private const int Block = KeccakHash.RateBlockLength;
    private const int Lanes = 25;
    private const int Blocks = 3;
    private const int KeyOffset = 8;

    [Test]
    public void Resumes_after_the_leading_blocks_an_edit_leaves_unchanged([Values(0, 1, 2, 3)] int editedBlock)
    {
        byte[] retained = Branch(seed: 1);
        Retain(retained);

        byte[] edited = (byte[])retained.Clone();
        edited[editedBlock * Block + Block / 2] ^= 0xff;

        AssertResumes(edited, expectedBlocks: Math.Min(editedBlock, Blocks), expectedEntry: 0);
    }

    [Test]
    public void Resumes_an_unchanged_input_after_all_its_whole_blocks()
    {
        byte[] retained = Branch(seed: 2);
        Retain(Branch(seed: 3), retained);

        AssertResumes((byte[])retained.Clone(), expectedBlocks: Blocks, expectedEntry: 1);
    }

    /// <remarks>Inputs sharing the key word share a probe sequence, so only the full first-block compare tells them apart.</remarks>
    [Test]
    public void Resumes_from_the_input_whose_first_block_matches_among_those_sharing_its_key()
    {
        byte[] first = Branch(seed: 4);
        byte[] second = Branch(seed: 5);
        CopyKey(from: first, to: second);
        Retain(first, second);

        AssertResumes((byte[])second.Clone(), expectedBlocks: Blocks, expectedEntry: 1);
        AssertResumes((byte[])first.Clone(), expectedBlocks: Blocks, expectedEntry: 0);
    }

    [Test]
    public void Misses_a_first_block_retained_under_no_input()
    {
        byte[] retained = Branch(seed: 6);
        byte[] other = Branch(seed: 7);
        CopyKey(from: retained, to: other);
        Retain(retained);

        AssertResumes(other, expectedBlocks: 0, expectedEntry: -1);
    }

    /// <remarks>The ninth input sharing a key finds its probe sequence full, so it is never placed and its lookup stays bounded.</remarks>
    [Test]
    public void Leaves_an_input_beyond_the_probe_bound_unplaced()
    {
        const int sharing = 9;
        byte[][] inputs = new byte[sharing][];
        for (int i = 0; i < sharing; i++)
        {
            inputs[i] = Branch(seed: 10 + i);
            CopyKey(from: inputs[0], to: inputs[i]);
        }

        Retain(inputs);

        AssertResumes((byte[])inputs[sharing - 2].Clone(), expectedBlocks: Blocks, expectedEntry: sharing - 2);
        AssertResumes((byte[])inputs[sharing - 1].Clone(), expectedBlocks: 0, expectedEntry: -1);
    }

    private static void Retain(params byte[][] inputs)
    {
        ulong[] states = new ulong[inputs.Length * Blocks * Lanes];
        for (int i = 0; i < states.Length; i++)
            states[i] = StateLane(i / (Blocks * Lanes), i / Lanes % Blocks, i % Lanes);

        KeccakHash.Retain(inputs, states);
    }

    private static void AssertResumes(byte[] input, int expectedBlocks, int expectedEntry)
    {
        ulong[] state = new ulong[Lanes];
        int blocks = KeccakHash.FindRetainedState(input, state);

        Assert.That(blocks, Is.EqualTo(expectedBlocks));
        if (expectedBlocks == 0) return;

        ulong[] expected = new ulong[Lanes];
        for (int lane = 0; lane < Lanes; lane++)
            expected[lane] = StateLane(expectedEntry, expectedBlocks - 1, lane);
        Assert.That(state, Is.EqualTo(expected));
    }

    private static ulong StateLane(int entry, int block, int lane) => (ulong)(entry * 10_000 + block * 100 + lane);

    private static void CopyKey(byte[] from, byte[] to) =>
        BinaryPrimitives.WriteUInt64LittleEndian(to.AsSpan(KeyOffset), BinaryPrimitives.ReadUInt64LittleEndian(from.AsSpan(KeyOffset)));

    private static byte[] Branch(int seed)
    {
        byte[] branch = new byte[Length];
        new Random(seed).NextBytes(branch);
        return branch;
    }
}
