// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Ethereum.Test.Base;

/// <summary>
/// Filters tests into chunks for parallel CI execution.
/// Set TEST_CHUNK environment variable to "1of3", "2of3", "3of3" etc.
/// Uses consistent hashing on test names to ensure deterministic partitioning.
/// </summary>
public static class TestChunkFilter
{
    private static (int Index, int Total)? GetChunkConfig()
    {
        string? chunkEnv = Environment.GetEnvironmentVariable("TEST_CHUNK");
        if (string.IsNullOrEmpty(chunkEnv))
            return null;

        string[] parts = chunkEnv.Split("of", StringSplitOptions.None);

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
    /// Filters a collection of tests based on the TEST_CHUNK environment variable.
    /// If TEST_CHUNK is not set, returns all tests.
    /// </summary>
    public static IEnumerable<T> FilterByChunk<T>(IEnumerable<T> tests, Func<T, string> getTestIdentifier)
    {
        var chunkConfig = GetChunkConfig();
        if (chunkConfig is null)
            return tests;

        // Materialize to ensure consistent enumeration
        var testList = tests as IList<T> ?? tests.ToList();
        var (index, total) = chunkConfig.Value;
        return testList.Where(test => GetChunkForTest(getTestIdentifier(test), total) == index);
    }

    /// <summary>
    /// Determines which chunk (1-based) a test belongs to based on its identifier.
    /// Uses a stable hash to ensure consistent assignment across runs.
    /// </summary>
    private static int GetChunkForTest(string testIdentifier, int totalChunks)
    {
        // Use a simple stable hash - sum of character codes
        // This is deterministic and doesn't depend on runtime specifics
        long hash = 0;
        foreach (char c in testIdentifier)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }

        return (int)(hash % totalChunks) + 1; // 1-based chunk index
    }
}
