// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Test;

/// <summary>
/// Partitions tests into chunks for parallel CI execution. Driven by the
/// <c>TEST_CHUNK</c> environment variable (format: <c>"1of4"</c>).
/// <see cref="FilterByChunk{T}"/> slices an enumeration by index;
/// <see cref="ShouldRunInChunk"/> picks per-test by stable name hash.
/// </summary>
public static class TestChunkFilter
{
    public static IEnumerable<T> FilterByChunk<T>(IEnumerable<T> tests)
    {
        (int Index, int Total)? chunkConfig = GetChunkConfig();

        if (chunkConfig is null)
        {
            return tests;
        }

        (int chunkIndex, int totalChunks) = chunkConfig.Value;
        return FilterByChunkIterator(tests, chunkIndex, totalChunks);
    }

    /// <summary>
    /// True when <paramref name="stableTestName"/> belongs to the currently
    /// selected chunk, or when no chunk is configured. Hash is deterministic
    /// across processes (unlike <see cref="string.GetHashCode()"/>).
    /// </summary>
    public static bool ShouldRunInChunk(string stableTestName)
    {
        (int Index, int Total)? chunkConfig = GetChunkConfig();

        if (chunkConfig is null)
        {
            return true;
        }

        (int chunkIndex, int totalChunks) = chunkConfig.Value;
        uint hash = StableHash(stableTestName);
        return (hash % (uint)totalChunks) == (uint)(chunkIndex - 1);
    }

    /// <summary>1-based chunk index and total, or null when <c>TEST_CHUNK</c> is unset.</summary>
    public static (int Index, int Total)? TryGetChunkConfig() => GetChunkConfig();

    private static IEnumerable<T> FilterByChunkIterator<T>(IEnumerable<T> tests, int chunkIndex, int totalChunks)
    {
        // Interleaved (test[i] → chunk i % N) so heavy tests don't cluster.
        int testIndex = 0;
        foreach (T test in tests)
        {
            if (testIndex % totalChunks == chunkIndex - 1)
            {
                yield return test;
            }

            testIndex++;
        }
    }

    private static (int Index, int Total)? GetChunkConfig()
    {
        string? chunkEnv = Environment.GetEnvironmentVariable("TEST_CHUNK");

        if (string.IsNullOrEmpty(chunkEnv))
        {
            return null;
        }

        string[] parts = chunkEnv.Split("of");

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int index) ||
            !int.TryParse(parts[1], out int total) ||
            index < 1 || index > total || total < 1)
        {
            throw new ArgumentException($"Invalid TEST_CHUNK format: '{chunkEnv}'. Expected format: '1of3', '2of5', etc.");
        }

        return (index, total);
    }

    // FNV-1a-style 32-bit hash, byte-by-byte over each UTF-16 char. Only property
    // we rely on: same input ⇒ same output across processes/platforms.
    private static uint StableHash(string value)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        uint hash = offsetBasis;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            hash ^= (byte)(c & 0xFF);
            hash *= prime;
            hash ^= (byte)((c >> 8) & 0xFF);
            hash *= prime;
        }

        return hash;
    }
}
