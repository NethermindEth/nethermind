// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

[Parallelizable(ParallelScope.All)]
public class KeccakHashBatchTests
{
    // keccak256("") — the canonical Ethereum empty-input hash.
    private const string EmptyKeccak = "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

    [Test]
    public void Empty_input_matches_known_vector()
    {
        int lanes = Vector<ulong>.Count;
        byte[] inputs = [];
        byte[] outputs = new byte[lanes * 32];

        KeccakHashBatch.ComputeHash256(inputs, 0, outputs);

        for (int k = 0; k < lanes; k++)
        {
            Assert.That(Convert.ToHexString(outputs.AsSpan(k * 32, 32)).ToLowerInvariant(), Is.EqualTo(EmptyKeccak), $"lane {k}");
        }
    }

    // Lengths chosen to exercise empty, sub-block, exact block multiples, and the bytes either side of
    // each rate boundary (136), where padding placement and block counts change.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(8)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(33)]
    [TestCase(135)]
    [TestCase(136)]
    [TestCase(137)]
    [TestCase(200)]
    [TestCase(271)]
    [TestCase(272)]
    [TestCase(273)]
    [TestCase(560)]
    [TestCase(561)]
    [TestCase(1000)]
    public void Each_lane_matches_scalar_keccak(int length)
    {
        int lanes = Vector<ulong>.Count;
        Random random = new(length * 7919 + 1); // deterministic, varies per case
        byte[] inputs = new byte[lanes * length];
        random.NextBytes(inputs);

        byte[] outputs = new byte[lanes * 32];
        KeccakHashBatch.ComputeHash256(inputs, length, outputs);

        for (int k = 0; k < lanes; k++)
        {
            byte[] expected = KeccakHash.ComputeHashBytes(inputs.AsSpan(k * length, length));
            Assert.That(outputs.AsSpan(k * 32, 32).ToArray(), Is.EqualTo(expected), $"lane {k}, length {length}");
        }
    }

    [Test]
    public void Lanes_are_independent_not_broadcast()
    {
        // Distinct per-lane inputs must produce distinct digests — guards against a lane-indexing bug
        // that would silently hash lane 0 into every output.
        int lanes = Vector<ulong>.Count;
        const int length = 64;
        byte[] inputs = new byte[lanes * length];
        for (int k = 0; k < lanes; k++)
        {
            inputs[k * length] = (byte)(k + 1);
        }

        byte[] outputs = new byte[lanes * 32];
        KeccakHashBatch.ComputeHash256(inputs, length, outputs);

        for (int k = 1; k < lanes; k++)
        {
            Assert.That(outputs.AsSpan(k * 32, 32).ToArray(), Is.Not.EqualTo(outputs.AsSpan(0, 32).ToArray()), $"lane {k} equals lane 0");
        }
    }

    // Local-only throughput comparison vs the scalar path. Excluded from CI (timing-dependent and
    // hardware-sensitive: the win scales with Vector<ulong>.Count — 2 on Arm/SSE2, 4 on AVX2, 8 on AVX-512).
    [Test, Explicit]
    public void Benchmark_batched_vs_scalar()
    {
        int lanes = Vector<ulong>.Count;
        const int length = 136; // one rate block — representative of a small trie node
        const int batches = 400_000;

        Random random = new(12345);
        byte[] inputs = new byte[lanes * length];
        random.NextBytes(inputs);
        byte[] outputs = new byte[lanes * 32];

        // warm up
        for (int i = 0; i < 1000; i++) KeccakHashBatch.ComputeHash256(inputs, length, outputs);

        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < batches; i++) KeccakHashBatch.ComputeHash256(inputs, length, outputs);
        double batchedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        Span<byte> scalarOut = stackalloc byte[32];
        for (int i = 0; i < 1000; i++)
            for (int k = 0; k < lanes; k++)
                KeccakHash.ComputeHash(inputs.AsSpan(k * length, length), scalarOut);

        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < batches; i++)
            for (int k = 0; k < lanes; k++)
                KeccakHash.ComputeHash(inputs.AsSpan(k * length, length), scalarOut);
        double scalarMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        long totalHashes = (long)batches * lanes;
        string line =
            $"KECCAK_BATCH lanes={lanes} hashes={totalHashes} scalar={scalarMs:0.#}ms batched={batchedMs:0.#}ms " +
            $"scalar_ns/hash={scalarMs * 1e6 / totalHashes:0.#} batched_ns/hash={batchedMs * 1e6 / totalHashes:0.#} speedup={scalarMs / batchedMs:0.##}x";
        TestContext.Progress.WriteLine(line);
        string? outPath = Environment.GetEnvironmentVariable("KECCAK_BATCH_OUT");
        if (outPath is not null)
        {
            System.IO.File.AppendAllText(outPath, line + Environment.NewLine);
        }
    }
}
