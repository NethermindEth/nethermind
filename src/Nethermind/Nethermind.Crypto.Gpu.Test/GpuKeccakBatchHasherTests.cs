// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Crypto.Gpu.Test;

[TestFixture]
public class GpuKeccakBatchHasherTests
{
    // Keccak256 of the empty input, per Ethereum's canonical constant.
    private const string EmptyInputHashHex = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

    // Acquires a GPU hasher or skips the test on a box without a suitable accelerator (e.g. CI).
    private static GpuKeccakBatchHasher RequireGpu()
    {
        if (!GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? hasher) || hasher is null)
        {
            Assert.Ignore("no GPU");
        }
        return hasher!;
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

    [Test]
    public void T4_1_RandomizedDifferential_MatchesPerInputCompute()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

        const int inputCount = 1000;
        int seed = Environment.TickCount;
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

        hasher.HashBatch(flat, offsets, actual);

        for (int i = 0; i < inputCount; i++)
        {
            ValueHash256 expected = ValueKeccak.Compute(inputs[i]);
            Assert.That(actual[i], Is.EqualTo(expected), $"seed={seed} index={i} length={inputs[i].Length}");
        }
    }

    [TestCase(new[] { 0, 1, 135, 136, 137, 271, 272, 273 }, TestName = "T4_2_RateBoundaries")]
    [TestCase(new[] { 2, 0, 1, 0 }, TestName = "T4_2_ZeroLengthMiddleAndEnd")]
    public void T4_2_BoundaryLengths_MatchesPerInputCompute(int[] lengths)
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

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

        hasher.HashBatch(flat, offsets, actual);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                ValueHash256 expected = ValueKeccak.Compute(inputs[i]);
                Assert.That(actual[i], Is.EqualTo(expected), $"index={i} length={lengths[i]}");
            }
        }
    }

    [Test]
    public void T4_3_EmptyInput_KnownAnswer()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

        ValueHash256 expected = new(EmptyInputHashHex);
        byte[] flat = [];
        int[] offsets = [0];
        ValueHash256[] actual = new ValueHash256[1];

        hasher.HashBatch(flat, offsets, actual);

        Assert.That(actual[0], Is.EqualTo(expected));
    }

    [Test]
    public void EmptyBatch_CompletesWithoutThrowing()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();
        Assert.DoesNotThrow(() => hasher.HashBatch(ReadOnlySpan<byte>.Empty, [], []));
    }

    [Test]
    public void WritesOnlyIntoTargetSlice_LeavesSentinelsUntouched()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

        ValueHash256 sentinel = new("0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        const int prefix = 2;
        const int inner = 3;
        const int suffix = 2;
        ValueHash256[] buffer = new ValueHash256[prefix + inner + suffix];
        buffer.AsSpan().Fill(sentinel);

        byte[][] inputs = [[0x01], [0x02, 0x02], []];
        (byte[] flat, int[] offsets) = Flatten(inputs);

        hasher.HashBatch(flat, offsets, buffer.AsSpan(prefix, inner));

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

    [Test]
    public void LengthMismatch_Throws()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();
        byte[] flat = [0x01];
        int[] offsets = [1];
        ValueHash256[] outputs = new ValueHash256[2];
        Assert.Throws<ArgumentException>(() => hasher.HashBatch(flat, offsets, outputs));
    }

    [Test]
    public void LastOffsetNotEqualToFlatLength_Throws()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();
        byte[] flat = [0x01, 0x02, 0x03];
        int[] offsets = [2];
        ValueHash256[] outputs = new ValueHash256[1];
        Assert.Throws<ArgumentException>(() => hasher.HashBatch(flat, offsets, outputs));
    }

    [TestCase(new byte[] { 1, 2, 3, 4, 5 }, new[] { 3, 2, 5 }, TestName = "NonMonotonicInteriorOffsets_Throws")]
    [TestCase(new byte[] { 1, 2, 3, 4 }, new[] { 5, 4 }, TestName = "OffsetExceedsFlatLength_Throws")]
    public void MalformedOffsets_ThrowArgumentException(byte[] flat, int[] offsets)
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();
        ValueHash256[] outputs = new ValueHash256[offsets.Length];
        Assert.Throws<ArgumentException>(() => hasher.HashBatch(flat, offsets, outputs));
    }

    // T7.1: transfer-path stress with 100k messages of widely mixed lengths (0..600), differential vs ValueKeccak.
    [Test]
    public void T7_1_LargeMixedLengthBatch_MatchesPerInputCompute()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

        const int inputCount = 100_000;
        int seed = Environment.TickCount;
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

        hasher.HashBatch(flat, offsets, actual);

        for (int i = 0; i < inputCount; i++)
        {
            ValueHash256 expected = ValueKeccak.Compute(inputs[i]);
            Assert.That(actual[i], Is.EqualTo(expected), $"seed={seed} index={i} length={inputs[i].Length}");
        }
    }

    // Deterministic permutation regression: same-length pairs with DISTINCT contents force a non-identity block-count
    // grouping permutation; a wrong unpermute (or transposed sort) would swap the digests of the two 200-byte messages.
    [Test]
    public void DeterministicPermutation_SameLengthDistinctContents_MatchesPerInputCompute()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

        byte[][] inputs = new byte[4][];
        inputs[0] = FilledArray(200, 0xA1);
        inputs[1] = FilledArray(1, 0xB2);
        inputs[2] = FilledArray(200, 0xC3); // same length as [0], different content
        inputs[3] = FilledArray(1, 0xD4);   // same length as [1], different content

        (byte[] flat, int[] offsets) = Flatten(inputs);
        ValueHash256[] actual = new ValueHash256[4];

        hasher.HashBatch(flat, offsets, actual);

        AssertAllMatch(inputs, actual);
    }

    // Buffer/staging reuse on ONE instance: a large batch grows the buffers, then a smaller batch must use the correct
    // subview (no stale tail), then a batch with zero-length messages must still hash correctly.
    [Test]
    public void BufferReuse_ShrinkingBatchesAndZeroLengthMessages_AllCorrect()
    {
        using GpuKeccakBatchHasher hasher = RequireGpu();

        RunAndAssert(hasher, RandomInputs(5000, seed: 7, minLen: 1, maxLen: 300));
        RunAndAssert(hasher, RandomInputs(64, seed: 8, minLen: 1, maxLen: 300)); // smaller: exercises grown-buffer subview
        RunAndAssert(hasher, [FilledArray(50, 0x11), [], FilledArray(1, 0x22), [], FilledArray(200, 0x33)]); // zero-length interior + end
    }

    private static void RunAndAssert(GpuKeccakBatchHasher hasher, byte[][] inputs)
    {
        (byte[] flat, int[] offsets) = Flatten(inputs);
        ValueHash256[] actual = new ValueHash256[inputs.Length];
        hasher.HashBatch(flat, offsets, actual);
        AssertAllMatch(inputs, actual);
    }

    private static void AssertAllMatch(byte[][] inputs, ReadOnlySpan<ValueHash256> actual)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                Assert.That(actual[i], Is.EqualTo(ValueKeccak.Compute(inputs[i])), $"index={i} length={inputs[i].Length}");
            }
        }
    }

    // The diagnostics/benchmark overload restricts to a device class: whatever it creates must hash correctly, and its
    // reported device name must not contradict the requested class. Skips green on hosts lacking that class (e.g. CI).
    [TestCase(AcceleratorTypePreference.Cuda, TestName = "TryCreate_Cuda_HashesCorrectly")]
    [TestCase(AcceleratorTypePreference.OpenCL, TestName = "TryCreate_OpenCL_HashesCorrectly")]
    public void TryCreate_WithPreference_HashesCorrectlyOrSkips(AcceleratorTypePreference preference)
    {
        if (!GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? hasher, preference) || hasher is null)
        {
            Assert.Ignore($"no {preference} accelerator");
        }

        using (hasher)
        {
            TestContext.Out.WriteLine($"{preference} -> {hasher!.AcceleratorName}");

            byte[][] inputs = [[], [0x01], FilledArray(200, 0x7E)];
            (byte[] flat, int[] offsets) = Flatten(inputs);
            ValueHash256[] actual = new ValueHash256[inputs.Length];

            hasher.HashBatch(flat, offsets, actual);

            AssertAllMatch(inputs, actual);
        }
    }

    // EnumerateDevices + the device-index overload: every enumerated device must create a hasher that hashes correctly.
    // Skips green on a box with no non-CPU accelerator (e.g. CI).
    [Test]
    public void EnumerateDevices_EachDeviceCreatesAndHashesCorrectly()
    {
        System.Collections.Generic.IReadOnlyList<GpuDeviceInfo> devices = GpuKeccakBatchHasher.EnumerateDevices();
        if (devices.Count == 0)
        {
            Assert.Ignore("no non-CPU accelerator");
        }

        byte[][] inputs = [[], [0x01], FilledArray(200, 0x7E)];
        (byte[] flat, int[] offsets) = Flatten(inputs);

        foreach (GpuDeviceInfo device in devices)
        {
            TestContext.Out.WriteLine($"device[{device.Index}] {device.Type} {device.Name} {device.MemoryBytes} bytes");
            Assert.That(GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? hasher, device.Index), Is.True, $"device {device.Index} must create");
            using (hasher)
            {
                ValueHash256[] actual = new ValueHash256[inputs.Length];
                hasher!.HashBatch(flat, offsets, actual);
                AssertAllMatch(inputs, actual);
            }
        }
    }

    // Out-of-range device index fails cleanly (false, null) rather than throwing.
    [Test]
    public void TryCreate_OutOfRangeDeviceIndex_ReturnsFalse()
    {
        bool ok = GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? hasher, int.MaxValue);
        using (hasher)
        {
            Assert.That(ok, Is.False);
            Assert.That(hasher, Is.Null);
        }
    }

    // The Any preference is exactly the parameterless overload: same success/failure and, on success, same device.
    [Test]
    public void TryCreate_Any_MatchesParameterlessOverload()
    {
        bool defaultOk = GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? viaDefault);
        bool anyOk = GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? viaAny, AcceleratorTypePreference.Any);

        using (viaDefault)
        using (viaAny)
        {
            Assert.That(anyOk, Is.EqualTo(defaultOk), "Any preference must agree with the parameterless overload on availability");
            if (defaultOk)
            {
                Assert.That(viaAny!.AcceleratorName, Is.EqualTo(viaDefault!.AcceleratorName), "Any preference must pick the same device");
            }
        }
    }

    private static byte[] FilledArray(int length, byte value)
    {
        byte[] a = new byte[length];
        Array.Fill(a, value);
        return a;
    }

    private static byte[][] RandomInputs(int count, int seed, int minLen, int maxLen)
    {
        Random rng = new(seed);
        byte[][] inputs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] input = new byte[rng.Next(minLen, maxLen + 1)];
            rng.NextBytes(input);
            inputs[i] = input;
        }
        return inputs;
    }
}
