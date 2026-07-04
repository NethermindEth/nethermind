// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

[TestFixture]
public class KeccakBatchHasherTests
{
    // Each registered backend is exercised by every test below; later phases add entries here.
    private static IEnumerable<TestCaseData> Backends()
    {
        yield return new TestCaseData((Func<IKeccakBatchHasher>)(static () => new PerMessageKeccakBatchHasher()))
            .SetName(nameof(PerMessageKeccakBatchHasher));
    }

    // Keccak256 of the empty input, per Ethereum's canonical constant.
    private const string EmptyInputHashHex = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

    // Named length scenarios for the boundary/edge differential; 135 exercises the combined 0x81 pad byte
    // (rate is 136), and consecutive zero lengths exercise zero-length inputs in the middle and at the end.
    private static IEnumerable<TestCaseData> BoundaryScenarios()
    {
        foreach (TestCaseData backend in Backends())
        {
            Func<IKeccakBatchHasher> factory = (Func<IKeccakBatchHasher>)backend.Arguments[0]!;
            string name = backend.TestName ?? "backend";

            yield return new TestCaseData(factory, new[] { 0, 1, 135, 136, 137, 271, 272, 273 })
                .SetName($"{name}_RateBoundaries");
            yield return new TestCaseData(factory, new[] { 2, 0, 1, 0 })
                .SetName($"{name}_ZeroLengthMiddleAndEnd");
        }
    }

    [TestCaseSource(nameof(Backends))]
    public void T4_1_RandomizedDifferential_MatchesPerInputCompute(Func<IKeccakBatchHasher> factory)
    {
        const int inputCount = 1000;
        int seed = Environment.TickCount;
        // Emit the seed before hashing so it survives even if HashBatch throws mid-batch.
        TestContext.Out.WriteLine($"seed={seed}");
        Random rng = new(seed);

        byte[][] inputs = new byte[inputCount][];
        for (int i = 0; i < inputCount; i++)
        {
            byte[] input = new byte[rng.Next(0, 601)];
            rng.NextBytes(input);
            inputs[i] = input;
        }

        (byte[] flat, int[] offsets) = Flatten(inputs);
        ValueHash256[] actual = new ValueHash256[inputCount];

        factory().HashBatch(flat, offsets, actual);

        for (int i = 0; i < inputCount; i++)
        {
            ValueHash256 expected = ValueKeccak.Compute(inputs[i]);
            Assert.That(actual[i], Is.EqualTo(expected), $"seed={seed} index={i} length={inputs[i].Length}");
        }
    }

    [TestCaseSource(nameof(BoundaryScenarios))]
    public void T4_2_BoundaryLengths_MatchesPerInputCompute(Func<IKeccakBatchHasher> factory, int[] lengths)
    {
        Random rng = new(1);

        byte[][] inputs = new byte[lengths.Length][];
        for (int i = 0; i < lengths.Length; i++)
        {
            byte[] input = new byte[lengths[i]];
            rng.NextBytes(input);
            inputs[i] = input;
        }

        (byte[] flat, int[] offsets) = Flatten(inputs);
        ValueHash256[] actual = new ValueHash256[lengths.Length];

        factory().HashBatch(flat, offsets, actual);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                ValueHash256 expected = ValueKeccak.Compute(inputs[i]);
                Assert.That(actual[i], Is.EqualTo(expected), $"index={i} length={lengths[i]}");
            }
        }
    }

    [TestCaseSource(nameof(Backends))]
    public void T4_3_EmptyInput_KnownAnswer(Func<IKeccakBatchHasher> factory)
    {
        ValueHash256 expected = new(EmptyInputHashHex);
        byte[] flat = [];
        int[] offsets = [0];
        ValueHash256[] actual = new ValueHash256[1];

        factory().HashBatch(flat, offsets, actual);

        Assert.That(actual[0], Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(Backends))]
    public void EmptyBatch_CompletesWithoutThrowing(Func<IKeccakBatchHasher> factory) =>
        Assert.DoesNotThrow(() => factory().HashBatch(ReadOnlySpan<byte>.Empty, [], []));

    [TestCaseSource(nameof(Backends))]
    public void WritesOnlyIntoTargetSlice_LeavesSentinelsUntouched(Func<IKeccakBatchHasher> factory)
    {
        // Sentinel-fill a larger buffer, hand HashBatch only the interior slice, and prove the outer slots
        // are never scribbled - the guard that catches a SIMD/GPU backend writing out of bounds.
        ValueHash256 sentinel = new("0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        const int prefix = 2;
        const int inner = 3;
        const int suffix = 2;
        ValueHash256[] buffer = new ValueHash256[prefix + inner + suffix];
        buffer.AsSpan().Fill(sentinel);

        byte[][] inputs = [[0x01], [0x02, 0x02], []]; // one 1-byte, one 2-byte, one empty
        (byte[] flat, int[] offsets) = Flatten(inputs);

        factory().HashBatch(flat, offsets, buffer.AsSpan(prefix, inner));

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < prefix; i++)
            {
                Assert.That(buffer[i], Is.EqualTo(sentinel), $"prefix slot {i} was overwritten");
            }
            for (int i = 0; i < suffix; i++)
            {
                Assert.That(buffer[prefix + inner + i], Is.EqualTo(sentinel), $"suffix slot {i} was overwritten");
            }
            for (int i = 0; i < inner; i++)
            {
                Assert.That(buffer[prefix + i], Is.EqualTo(ValueKeccak.Compute(inputs[i])), $"inner slot {i}");
            }
        }
    }

    [TestCaseSource(nameof(Backends))]
    public void LengthMismatch_Throws(Func<IKeccakBatchHasher> factory)
    {
        byte[] flat = [0x01];
        int[] offsets = [1];
        ValueHash256[] outputs = new ValueHash256[2]; // deliberately longer than offsets

        Assert.Throws<ArgumentException>(() => factory().HashBatch(flat, offsets, outputs));
    }

    [TestCaseSource(nameof(Backends))]
    public void LastOffsetNotEqualToFlatLength_Throws(Func<IKeccakBatchHasher> factory)
    {
        byte[] flat = [0x01, 0x02, 0x03]; // 3 bytes, but last offset stops at 2 (trailing byte ignored)
        int[] offsets = [2];
        ValueHash256[] outputs = new ValueHash256[1];

        Assert.Throws<ArgumentException>(() => factory().HashBatch(flat, offsets, outputs));
    }

    // Concatenates inputs and builds the exclusive-end offset array the hasher expects.
    private static (byte[] flat, int[] offsets) Flatten(byte[][] inputs)
    {
        int[] offsets = new int[inputs.Length];
        int total = 0;
        for (int i = 0; i < inputs.Length; i++)
        {
            total += inputs[i].Length;
            offsets[i] = total;
        }

        byte[] flat = new byte[total];
        int pos = 0;
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i].CopyTo(flat.AsSpan(pos));
            pos += inputs[i].Length;
        }

        return (flat, offsets);
    }
}
