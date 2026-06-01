// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Ethereum.Test.Base;

/// <summary>
/// Filters tests into chunks for parallel CI execution.
/// Set TEST_CHUNK environment variable to "1of3", "2of3", "3of3" etc.
/// Two partitioning modes are supported:
///   - <see cref="FilterByChunk{T}"/> for index-based partitioning over an
///     ordered enumeration (used by [TestCaseSource]-driven projects).
///   - <see cref="ShouldRunInChunk(string)"/> for hash-based partitioning by
///     stable test name (used by [Test]/[TestCase] projects via an assembly
///     <c>ITestAction</c>).
/// </summary>
public static class TestChunkFilter
{
    internal static bool IsEnabled => GetChunkConfig() is not null;

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
    /// Returns true when the given test name belongs to the currently selected
    /// chunk, or when no chunk is configured (no-op pass-through).
    /// Partitioning is deterministic across runs and processes — it uses a
    /// stable FNV-1a hash of <paramref name="stableTestName"/> rather than
    /// <see cref="string.GetHashCode()"/> (which is randomized per-process).
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

    /// <summary>
    /// Returns the currently selected chunk (1-based index, total) or null
    /// when <c>TEST_CHUNK</c> is unset.
    /// </summary>
    public static (int Index, int Total)? TryGetChunkConfig() => GetChunkConfig();

    private static IEnumerable<T> FilterByChunkIterator<T>(IEnumerable<T> tests, int chunkIndex, int totalChunks)
    {
        // Interleaved distribution: test 0→chunk 1, test 1→chunk 2, etc.
        // Spreads heavy tests evenly across chunks instead of clustering them.
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

    /// <summary>
    /// 32-bit FNV-1a hash. Deterministic across processes and platforms,
    /// unlike <see cref="string.GetHashCode()"/> which is randomized.
    /// </summary>
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
