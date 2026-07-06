// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Fail-fast correctness gate for every discovered batch-keccak backend (including the experimental
/// <see cref="ParallelMultiBufferKeccakBatchHasher"/>): hashes a randomized 10k mixed-length batch through each backend
/// and asserts byte-equality against <see cref="ValueKeccak.Compute"/> per message. Invoked via the runner
/// <c>--verify-keccak-batch</c> before any timing, so a variant is proven correct before it is measured.
/// </summary>
public static class KeccakBatchBackendVerifier
{
    public static void Run()
    {
        Console.WriteLine("=== Keccak batch backend correctness gate (randomized 10k mixed-length differential) ===");

        const int count = 10_000;
        int seed = 12345;
        Random rng = new(seed);

        byte[][] inputs = new byte[count][];
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            // Mixed lengths incl. zero-length, sub-block, exact-block-boundary (135/136/137), and multi-block up to ~4 blocks.
            int len = (i % 11) switch
            {
                0 => 0,
                1 => 1,
                2 => 135,
                3 => 136,
                4 => 137,
                5 => 271,
                6 => 272,
                7 => 70,
                8 => 110,
                9 => 532,
                _ => rng.Next(0, 700),
            };
            byte[] input = new byte[len];
            rng.NextBytes(input);
            inputs[i] = input;
            total += len;
        }

        byte[] flat = new byte[total];
        int[] offsets = new int[count];
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            inputs[i].CopyTo(flat.AsSpan(pos));
            pos += inputs[i].Length;
            offsets[i] = pos;
        }

        ValueHash256[] expected = new ValueHash256[count];
        for (int i = 0; i < count; i++) expected[i] = ValueKeccak.Compute(inputs[i]);

        KeccakBatchBackend[] backends = KeccakBatchBackendCatalog.Discover();
        int failures = 0;
        try
        {
            foreach (KeccakBatchBackend backend in backends)
            {
                ValueHash256[] actual = new ValueHash256[count];
                backend.Hasher.HashBatch(flat, offsets, actual);

                int mismatch = -1;
                for (int i = 0; i < count; i++)
                {
                    if (!actual[i].Equals(expected[i])) { mismatch = i; break; }
                }

                if (mismatch < 0)
                {
                    Console.WriteLine($"  PASS  {backend.Name}");
                }
                else
                {
                    failures++;
                    Console.WriteLine($"  FAIL  {backend.Name}: first mismatch at index {mismatch} (len {inputs[mismatch].Length})");
                    Console.WriteLine($"        expected {expected[mismatch]}");
                    Console.WriteLine($"        actual   {actual[mismatch]}");
                }
            }
        }
        finally
        {
            KeccakBatchBackendCatalog.DisposeAll(backends);
        }

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("ALL BACKENDS CORRECT.");
        }
        else
        {
            Console.WriteLine($"{failures} BACKEND(S) FAILED - do not trust any timing for the failing backend(s).");
            Environment.ExitCode = 1;
        }
    }
}
