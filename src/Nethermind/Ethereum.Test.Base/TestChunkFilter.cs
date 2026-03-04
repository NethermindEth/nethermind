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
    public static IEnumerable<T> FilterByChunk<T>(IEnumerable<T> tests)
    {
        (int Index, int Total)? chunkConfig = GetChunkConfig();

        if (chunkConfig is null)
        {
            return tests;
        }

        ICollection<T> testList = tests as ICollection<T> ?? [.. tests];
        (int chunkIndex, int totalChunks) = chunkConfig.Value;

        int count = testList.Count;
        int chunkSize = count / totalChunks;
        int remainder = count % totalChunks;

        int skip = (chunkIndex - 1) * chunkSize;
        int take = chunkSize + (chunkIndex == totalChunks ? remainder : 0);

        return testList.Skip(skip).Take(take);

        static (int Index, int Total)? GetChunkConfig()
        {
            string? chunkEnv = Environment.GetEnvironmentVariable("TEST_CHUNK");

            if (string.IsNullOrEmpty(chunkEnv))
            {
                return null;
            }

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
    }
}
