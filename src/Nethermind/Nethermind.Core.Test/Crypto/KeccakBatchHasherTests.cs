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

        // Threshold 1 forces the multi-core partition path even for the tiny boundary/edge batches.
        yield return new TestCaseData((Func<IKeccakBatchHasher>)(static () => new ParallelKeccakBatchHasher(parallelThreshold: 1)))
            .SetName($"{nameof(ParallelKeccakBatchHasher)}_Parallel");
        // Default-threshold instance covers the inline calling-thread fall-through for small batches.
        yield return new TestCaseData((Func<IKeccakBatchHasher>)(static () => new ParallelKeccakBatchHasher()))
            .SetName($"{nameof(ParallelKeccakBatchHasher)}_Inline");

        // Vertical multi-buffer kernel, both grouping strategies; skipped-green where the ISA is unavailable.
        yield return new TestCaseData((Func<IKeccakBatchHasher>)(static () => MakeMultiBuffer(MultiBufferGroupingStrategy.UniformGroups)))
            .SetName($"{nameof(MultiBufferKeccakBatchHasher)}_UniformGroups");
        yield return new TestCaseData((Func<IKeccakBatchHasher>)(static () => MakeMultiBuffer(MultiBufferGroupingStrategy.RunToMaxSnapshots)))
            .SetName($"{nameof(MultiBufferKeccakBatchHasher)}_RunToMaxSnapshots");
    }

    // Skips the whole case when the vertical kernel's ISA is unavailable rather than instantiating a backend that cannot run.
    private static IKeccakBatchHasher MakeMultiBuffer(MultiBufferGroupingStrategy strategy)
    {
        if (!MultiBufferKeccakBatchHasher.IsSupported) Assert.Ignore("ISA not supported");
        return new MultiBufferKeccakBatchHasher(strategy);
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

    // Regression for the pointer path: malformed offsets that clear the O(1) last-offset guard must still be rejected
    // by the full validation scan (an interior descent or an out-of-bounds start), not read past the pinned buffer.
    // Both cases keep the last offset == flat.Length so only the up-front scan can catch them; threshold 1 forces parallel.
    [TestCase(new byte[] { 1, 2, 3, 4, 5 }, new[] { 3, 2, 5 }, TestName = "Parallel_NonMonotonicInteriorOffsets_Throws")]
    [TestCase(new byte[] { 1, 2, 3, 4 }, new[] { 5, 4 }, TestName = "Parallel_OffsetExceedsFlatLength_Throws")]
    public void ParallelPath_MalformedOffsets_ThrowArgumentException(byte[] flat, int[] offsets)
    {
        ParallelKeccakBatchHasher hasher = new(parallelThreshold: 1);
        ValueHash256[] outputs = new ValueHash256[offsets.Length];

        Assert.Throws<ArgumentException>(() => hasher.HashBatch(flat, offsets, outputs));
    }

    // Group-dispatch edges specific to the vertical kernel: how it splits a batch into 8-wide lane groups and remainders.
    // 9 equal-length -> one full 8-group + a 1-message remainder; a span of 4 distinct block counts; 7 -> all remainder;
    // a genuinely mixed 8-run that forces the RunToMaxSnapshots active/finished-lane path deterministically.
    private static IEnumerable<TestCaseData> GroupDispatchScenarios()
    {
        foreach (MultiBufferGroupingStrategy strategy in (MultiBufferGroupingStrategy[])[MultiBufferGroupingStrategy.UniformGroups, MultiBufferGroupingStrategy.RunToMaxSnapshots])
        {
            int[] nineEqual = new int[9];
            Array.Fill(nineEqual, 100); // all 1-block

            // 8 of each of 4 block counts. BlockCount = len/136 + 1, so 1-block=100, 2-block=240, 3-block=380, 4-block=520.
            int[] fourCounts = new int[32];
            for (int b = 0; b < 4; b++)
            {
                for (int k = 0; k < 8; k++) fourCounts[b * 8 + k] = 100 + b * 136 + b * 4;
            }

            int[] sevenRemainder = new int[7];
            Array.Fill(sevenRemainder, 50); // fewer than 8 -> whole batch is remainder

            // Exactly 8 messages of two different block counts (4 one-block=100B, 4 two-block=240B). Sorted by block
            // count they form ONE mixed 8-run, so RunToMaxSnapshots runs to 2 blocks with the four 1-block lanes going
            // inactive after block 1 - exercising the mid-run snapshot/absorb-nothing path deterministically.
            int[] mixedRun = [100, 240, 100, 240, 100, 240, 100, 240];

            yield return new TestCaseData(strategy, nineEqual).SetName($"{strategy}_NineEqual_FullGroupPlusRemainder");
            yield return new TestCaseData(strategy, fourCounts).SetName($"{strategy}_SpanFourBlockCounts");
            yield return new TestCaseData(strategy, sevenRemainder).SetName($"{strategy}_SevenAllRemainder");
            yield return new TestCaseData(strategy, mixedRun).SetName($"{strategy}_MixedEightRun");
        }
    }

    [TestCaseSource(nameof(GroupDispatchScenarios))]
    public void GroupDispatch_MatchesPerInputCompute(MultiBufferGroupingStrategy strategy, int[] lengths)
    {
        if (!MultiBufferKeccakBatchHasher.IsSupported) Assert.Ignore("ISA not supported");

        Random rng = new(42);
        byte[][] inputs = new byte[lengths.Length][];
        for (int i = 0; i < lengths.Length; i++)
        {
            inputs[i] = new byte[lengths[i]];
            rng.NextBytes(inputs[i]);
        }

        (byte[] flat, int[] offsets) = Flatten(inputs);
        ValueHash256[] actual = new ValueHash256[lengths.Length];

        new MultiBufferKeccakBatchHasher(strategy).HashBatch(flat, offsets, actual);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                Assert.That(actual[i], Is.EqualTo(ValueKeccak.Compute(inputs[i])), $"index={i} length={lengths[i]}");
            }
        }
    }

    // Proves MixedEightRun is not a false positive: the block-count sort must place all 8 messages in a single run that
    // spans more than one block count, so RunToMaxSnapshots actually exercises its mid-run active/finished-lane path.
    [Test]
    public void MixedEightRun_SortsIntoOneRunSpanningTwoBlockCounts()
    {
        int[] lengths = [100, 240, 100, 240, 100, 240, 100, 240];
        int[] offsets = new int[lengths.Length];
        int total = 0;
        for (int i = 0; i < lengths.Length; i++) { total += lengths[i]; offsets[i] = total; }

        int[] permutation = new int[lengths.Length];
        int[] boundaries = new int[KeccakBatchGrouping.MaxGroups(lengths.Length)];
        int groups = KeccakBatchGrouping.GroupByBlockCount(offsets, permutation, boundaries);

        int[] sortedBlockCounts = new int[lengths.Length];
        for (int p = 0; p < permutation.Length; p++)
        {
            sortedBlockCounts[p] = KeccakBatchGrouping.BlockCount(lengths[permutation[p]]);
        }

        using (Assert.EnterMultipleScope())
        {
            // 8 messages -> a single 8-wide run; that run must contain both block counts (1 and 2), i.e. it is mixed.
            Assert.That(lengths, Has.Length.EqualTo(8), "run width");
            Assert.That(sortedBlockCounts[0], Is.EqualTo(1), "run starts at the smallest block count");
            Assert.That(sortedBlockCounts[^1], Is.EqualTo(2), "run ends at a larger block count");
            Assert.That(groups, Is.EqualTo(2), "two distinct block counts present");
        }
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
