// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Ethereum.Test.Base;

/// <summary>
/// Filters tests into chunks for parallel CI execution.
/// Set TEST_CHUNK environment variable to "1of3", "2of3", "3of3" etc.
/// Partitions tests by index for consistent and even distribution.
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

        // Materialize to ensure consistent enumeration and index-based chunking
        var testList = tests as IList<T> ?? tests.ToList();
        var (chunkIndex, totalChunks) = chunkConfig.Value;

        int count = testList.Count;
        int chunkSize = count / totalChunks;
        int remainder = count % totalChunks;

        // Calculate start and end indices for this chunk
        // Distribute remainder across first chunks (one extra item each)
        int start = (chunkIndex - 1) * chunkSize + Math.Min(chunkIndex - 1, remainder);
        int end = start + chunkSize + (chunkIndex <= remainder ? 1 : 0);

        return testList.Skip(start).Take(end - start);
    }
}
