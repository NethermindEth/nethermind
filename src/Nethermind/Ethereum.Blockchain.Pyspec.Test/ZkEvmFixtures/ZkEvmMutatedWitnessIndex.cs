// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

/// <summary>
/// Indexes which EIP-8025 "mutated" (deliberately corrupted) witnesses each zkEVM test carries. The marker
/// only exists in the <c>blockchain_tests_engine</c> format, so this scans the engine tree once and lets the
/// RLP loader stamp the equivalent blocks, keeping witness comparison from having to hardcode test names.
/// </summary>
/// <remarks>Relies on RLP <c>blocks[i]</c> aligning 1:1 with engine <c>engineNewPayloads[i]</c>.</remarks>
internal static class ZkEvmMutatedWitnessIndex
{
    private static readonly Lazy<IReadOnlyDictionary<string, MutatedInfo>> MutatedWitnessesByTest = new(Build);

    private readonly record struct MutatedInfo(int PayloadCount, HashSet<int> MutatedIndices);

    public static IEnumerable<BlockchainTest> StampMutatedBlocks(IEnumerable<BlockchainTest> tests)
    {
        IReadOnlyDictionary<string, MutatedInfo> index = MutatedWitnessesByTest.Value;
        foreach (BlockchainTest test in tests)
        {
            if (test.Blocks is { Length: > 0 } blocks && test.Name is not null
                && index.TryGetValue(Key(test.Category, test.Name), out MutatedInfo info))
            {
                if (blocks.Length != info.PayloadCount)
                    throw new InvalidOperationException(
                        $"zkEVM mutated-witness index misaligned for '{Key(test.Category, test.Name)}': " +
                        $"{blocks.Length} RLP blocks vs {info.PayloadCount} engine payloads.");

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (info.MutatedIndices.Contains(i))
                        blocks[i].ExecutionWitnessMutated = true;
                }
            }
            yield return test;
        }
    }

    private static string Key(string category, string name)
        => (category ?? string.Empty) + "::" + name.Replace("blockchain_test_engine", "blockchain_test");

    private static (string Category, string Name) SplitKey(string key)
    {
        key = key.Replace('\\', '/');
        int i = key.IndexOf(".py::", StringComparison.Ordinal);
        return i < 0 ? (string.Empty, key) : (key[..i], key[(i + 5)..]);
    }

    private static IReadOnlyDictionary<string, MutatedInfo> Build()
    {
        Dictionary<string, MutatedInfo> index = [];

        string cacheDir = TestFixtureDownloader.EnsureDownloaded(
            "PyTests", global::Ethereum.Blockchain.Pyspec.Test.Constants.ARCHIVE_URL_TEMPLATE, Constants.ArchiveVersion, Constants.ArchiveName);
        string engineDir = Path.Combine(cacheDir, "fixtures", "blockchain_tests_engine");
        if (!Directory.Exists(engineDir))
            return index;

        foreach (string file in Directory.EnumerateFiles(engineDir, "*.json", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            // Prefilter: only a handful of files carry the marker, so avoid deserializing the whole tree.
            if (!text.Contains("executionWitnessMutated", StringComparison.Ordinal))
                continue;

            using JsonDocument doc = JsonDocument.Parse(text);
            foreach (JsonProperty testProperty in doc.RootElement.EnumerateObject())
            {
                if (!testProperty.Value.TryGetProperty("engineNewPayloads", out JsonElement payloads)
                    || payloads.ValueKind != JsonValueKind.Array)
                    continue;

                HashSet<int> mutatedIndices = null;
                int payloadCount = 0;
                foreach (JsonElement payload in payloads.EnumerateArray())
                {
                    if (payload.TryGetProperty("executionWitnessMutated", out JsonElement mutated)
                        && mutated.ValueKind == JsonValueKind.True)
                        (mutatedIndices ??= []).Add(payloadCount);
                    payloadCount++;
                }

                if (mutatedIndices is not null)
                {
                    (string category, string name) = SplitKey(testProperty.Name);
                    index[Key(category, name)] = new MutatedInfo(payloadCount, mutatedIndices);
                }
            }
        }

        return index;
    }
}
