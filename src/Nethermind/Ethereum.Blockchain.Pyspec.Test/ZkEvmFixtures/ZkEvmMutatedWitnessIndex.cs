// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

/// <summary>
/// Data-driven source of truth for which zkEVM witnesses are EIP-8025 "mutated" (deliberately
/// corrupted to drive a stateless validator's reject path). The marker only exists in the
/// <c>blockchain_tests_engine</c> format (<c>engineNewPayloads[i].executionWitnessMutated</c>); the
/// RLP <c>blockchain_tests</c> format ships the same corrupted witness with no marker. This index
/// scans the engine tree once and lets the RLP loader stamp the equivalent blocks so witness
/// comparison can skip them without hardcoding test names.
/// </summary>
/// <remarks>
/// Invariant: both formats describe the same test/block sequence (ids differ only by the
/// <c>blockchain_test</c> ↔ <c>blockchain_test_engine</c> token) and RLP block index aligns 1:1
/// with engine payload index, so mutated <c>engineNewPayloads[i]</c> maps to RLP <c>blocks[i]</c>.
/// </remarks>
internal static class ZkEvmMutatedWitnessIndex
{
    // normalized test name -> block indices whose witness is mutated.
    private static readonly Lazy<IReadOnlyDictionary<string, HashSet<int>>> s_index = new(Build);

    /// <summary>Stamps <see cref="TestBlockJson.ExecutionWitnessMutated"/> on the RLP blocks that the
    /// engine fixtures flag as mutated, then yields each test through unchanged.</summary>
    public static IEnumerable<BlockchainTest> StampMutatedBlocks(IEnumerable<BlockchainTest> tests)
    {
        IReadOnlyDictionary<string, HashSet<int>> index = s_index.Value;
        foreach (BlockchainTest test in tests)
        {
            if (test.Blocks is { Length: > 0 } blocks && test.Name is not null
                && index.TryGetValue(Normalize(test.Name), out HashSet<int> mutatedIndices))
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (mutatedIndices.Contains(i))
                        blocks[i].ExecutionWitnessMutated = true;
                }
            }
            yield return test;
        }
    }

    // BlockchainTest.Name is the fixture key AFTER ".py::" (see JsonToEthereumTest.GetNameAndCategory),
    // so reduce the engine JSON key the same way, then collapse "blockchain_test_engine" onto
    // "blockchain_test" so both formats' ids match (also covers the *_from_state_test variants).
    private static string Normalize(string testKeyOrName)
    {
        int i = testKeyOrName.IndexOf(".py::", StringComparison.Ordinal);
        string name = i < 0 ? testKeyOrName : testKeyOrName[(i + 5)..];
        return name.Replace("blockchain_test_engine", "blockchain_test");
    }

    private static IReadOnlyDictionary<string, HashSet<int>> Build()
    {
        Dictionary<string, HashSet<int>> index = [];

        // Same args as the loader → reuses the loader's cached download dir.
        string cacheDir = TestFixtureDownloader.EnsureDownloaded(
            "PyTests", global::Ethereum.Blockchain.Pyspec.Test.Constants.ARCHIVE_URL_TEMPLATE, Constants.ArchiveVersion, Constants.ArchiveName);
        string engineDir = Path.Combine(cacheDir, "fixtures", "blockchain_tests_engine");
        if (!Directory.Exists(engineDir))
            return index; // engine tree absent (only blockchain_tests seeded) — degrade gracefully.

        foreach (string file in Directory.EnumerateFiles(engineDir, "*.json", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            // Prefilter: only ~25 payloads tree-wide carry the marker, so parse only the few files
            // mentioning it rather than deserializing all ~2.8k.
            if (!text.Contains("executionWitnessMutated", StringComparison.Ordinal))
                continue;

            using JsonDocument doc = JsonDocument.Parse(text);
            foreach (JsonProperty testProperty in doc.RootElement.EnumerateObject())
            {
                if (!testProperty.Value.TryGetProperty("engineNewPayloads", out JsonElement payloads)
                    || payloads.ValueKind != JsonValueKind.Array)
                    continue;

                HashSet<int> mutatedIndices = null;
                int i = 0;
                foreach (JsonElement payload in payloads.EnumerateArray())
                {
                    if (payload.TryGetProperty("executionWitnessMutated", out JsonElement mutated)
                        && mutated.ValueKind == JsonValueKind.True)
                        (mutatedIndices ??= []).Add(i);
                    i++;
                }

                if (mutatedIndices is not null)
                    index[Normalize(testProperty.Name)] = mutatedIndices;
            }
        }

        return index;
    }
}
