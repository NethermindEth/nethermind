// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Ethereum.Test.Base;

/// <summary>
/// Filters tests into chunks for parallel CI execution.
/// Set TEST_CHUNK environment variable to "1of3", "2of3", "3of3" etc.
/// Partitions tests by index for consistent and even distribution.
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
}
