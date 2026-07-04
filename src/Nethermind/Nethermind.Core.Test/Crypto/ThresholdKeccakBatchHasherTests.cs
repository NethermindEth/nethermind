// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

[TestFixture]
public class ThresholdKeccakBatchHasherTests
{
    // Records which backend a batch was routed to and always produces correct hashes via a real per-message backend.
    private sealed class RecordingHasher : IKeccakBatchHasher
    {
        private readonly PerMessageKeccakBatchHasher _inner = new();
        public int Calls { get; private set; }

        public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
        {
            Calls++;
            _inner.HashBatch(flat, offsets, outputs);
        }
    }

    // Fails on every call; used to prove the router contains a flaky fast backend and never calls it again.
    private sealed class ThrowingHasher : IKeccakBatchHasher
    {
        public int Calls { get; private set; }

        public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
        {
            Calls++;
            throw new InvalidOperationException("simulated GPU failure");
        }
    }

    private static (byte[] flat, int[] offsets) MakeBatch(int count, int length)
    {
        Random rng = new(count * 1000 + length);
        int[] offsets = new int[count];
        byte[] flat = new byte[count * length];
        for (int i = 0; i < count; i++)
        {
            rng.NextBytes(flat.AsSpan(i * length, length));
            offsets[i] = (i + 1) * length;
        }
        return (flat, offsets);
    }

    private static void AssertHashesCorrect(byte[] flat, int[] offsets, ReadOnlySpan<ValueHash256> actual, int length)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < offsets.Length; i++)
            {
                ValueHash256 expected = ValueKeccak.Compute(flat.AsSpan(i * length, length));
                Assert.That(actual[i], Is.EqualTo(expected), $"index={i}");
            }
        }
    }

    [Test]
    public void RoutesLargeBatchToFastBackend()
    {
        RecordingHasher fast = new();
        RecordingHasher cpu = new();
        ThresholdKeccakBatchHasher router = new(fast, cpu, minBatch: 16, LimboLogs.Instance);

        (byte[] flat, int[] offsets) = MakeBatch(32, 40);
        ValueHash256[] outputs = new ValueHash256[32];

        router.HashBatch(flat, offsets, outputs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Calls, Is.EqualTo(1), "fast backend should be used at/above threshold");
            Assert.That(cpu.Calls, Is.EqualTo(0), "cpu fallback should be untouched");
        }
        AssertHashesCorrect(flat, offsets, outputs, 40);
    }

    [Test]
    public void RoutesSmallBatchToCpuFallback()
    {
        RecordingHasher fast = new();
        RecordingHasher cpu = new();
        ThresholdKeccakBatchHasher router = new(fast, cpu, minBatch: 16, LimboLogs.Instance);

        (byte[] flat, int[] offsets) = MakeBatch(8, 40);
        ValueHash256[] outputs = new ValueHash256[8];

        router.HashBatch(flat, offsets, outputs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cpu.Calls, Is.EqualTo(1), "cpu fallback should be used below threshold");
            Assert.That(fast.Calls, Is.EqualTo(0), "fast backend should be untouched below threshold");
        }
        AssertHashesCorrect(flat, offsets, outputs, 40);
    }

    [Test]
    public void BatchExactlyAtThresholdUsesFastBackend()
    {
        RecordingHasher fast = new();
        RecordingHasher cpu = new();
        ThresholdKeccakBatchHasher router = new(fast, cpu, minBatch: 16, LimboLogs.Instance);

        (byte[] flat, int[] offsets) = MakeBatch(16, 40);
        ValueHash256[] outputs = new ValueHash256[16];

        router.HashBatch(flat, offsets, outputs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Calls, Is.EqualTo(1), "threshold is inclusive");
            Assert.That(cpu.Calls, Is.EqualTo(0));
        }
    }

    [Test]
    public void FastBackendThrow_FallsBackToCpuForThatBatch_AndProducesCorrectHashes()
    {
        ThrowingHasher fast = new();
        RecordingHasher cpu = new();
        ThresholdKeccakBatchHasher router = new(fast, cpu, minBatch: 16, LimboLogs.Instance);

        (byte[] flat, int[] offsets) = MakeBatch(32, 40);
        ValueHash256[] outputs = new ValueHash256[32];

        Assert.DoesNotThrow(() => router.HashBatch(flat, offsets, outputs));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Calls, Is.EqualTo(1), "fast backend attempted once");
            Assert.That(cpu.Calls, Is.EqualTo(1), "cpu produced the result after the fast backend threw");
        }
        AssertHashesCorrect(flat, offsets, outputs, 40);
    }

    [Test]
    public void FastBackendThrowOnce_PermanentlyFallsBackToCpuThereafter()
    {
        ThrowingHasher fast = new();
        RecordingHasher cpu = new();
        ThresholdKeccakBatchHasher router = new(fast, cpu, minBatch: 16, LimboLogs.Instance);

        // First large batch trips the fault; all subsequent large batches must skip the fast backend entirely.
        for (int round = 0; round < 5; round++)
        {
            (byte[] flat, int[] offsets) = MakeBatch(32, 40);
            ValueHash256[] outputs = new ValueHash256[32];
            router.HashBatch(flat, offsets, outputs);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Calls, Is.EqualTo(1), "fast backend must never be retried after a single failure");
            Assert.That(cpu.Calls, Is.EqualTo(5), "every batch after the fault is served by the cpu backend");
        }
    }

    [Test]
    public void MalformedOffsets_ThrowToCaller_AndDoNotRetireFastBackend()
    {
        RecordingHasher fast = new();
        RecordingHasher cpu = new();
        ThresholdKeccakBatchHasher router = new(fast, cpu, minBatch: 4, LimboLogs.Instance);

        // Interior non-monotonic offsets with a correct last offset: only the up-front contract scan can reject these.
        byte[] badFlat = [1, 2, 3, 4, 5];
        int[] badOffsets = [3, 2, 5]; // last == flat length, but descends 3 -> 2
        ValueHash256[] badOutputs = new ValueHash256[3];

        Assert.Throws<ArgumentException>(() => router.HashBatch(badFlat, badOffsets, badOutputs));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Calls, Is.EqualTo(0), "contract error must not reach the fast backend");
            Assert.That(cpu.Calls, Is.EqualTo(0), "contract error must not reach the cpu backend");
        }

        // A following valid large batch must still route to the fast backend - the contract error did not retire it.
        (byte[] flat, int[] offsets) = MakeBatch(8, 40);
        ValueHash256[] outputs = new ValueHash256[8];
        router.HashBatch(flat, offsets, outputs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Calls, Is.EqualTo(1), "fast backend still live after a caller contract error");
            Assert.That(cpu.Calls, Is.EqualTo(0));
        }
        AssertHashesCorrect(flat, offsets, outputs, 40);
    }
}
