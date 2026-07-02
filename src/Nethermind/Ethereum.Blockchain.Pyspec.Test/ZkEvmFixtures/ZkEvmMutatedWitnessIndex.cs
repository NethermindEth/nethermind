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
    // composite "category::name" key -> mutated-witness info for that test.
    private static readonly Lazy<IReadOnlyDictionary<string, MutatedInfo>> s_index = new(Build);

    /// <summary>The mutated-witness footprint of a single engine test: how many payloads it declared and which of
    /// their indices are marked mutated.</summary>
    private readonly record struct MutatedInfo(int PayloadCount, HashSet<int> MutatedIndices);

    /// <summary>Stamps <see cref="TestBlockJson.ExecutionWitnessMutated"/> on the RLP blocks that the
    /// engine fixtures flag as mutated, then yields each test through unchanged.</summary>
    public static IEnumerable<BlockchainTest> StampMutatedBlocks(IEnumerable<BlockchainTest> tests)
    {
        IReadOnlyDictionary<string, MutatedInfo> index = s_index.Value;
        foreach (BlockchainTest test in tests)
        {
            if (test.Blocks is { Length: > 0 } blocks && test.Name is not null
                && index.TryGetValue(Key(test.Category, test.Name), out MutatedInfo info))
            {
                // The blocks[i] == engineNewPayloads[i] mapping is the whole basis for stamping by index; a count
                // mismatch means that assumption broke (fixture format drift), so fail loudly rather than mis-stamp.
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

    // Key on category + name (not name alone): BlockchainTest.Name is only the part AFTER ".py::", so same-named
    // functions in different source dirs would otherwise collide and cross-stamp each other's blocks. Collapse
    // "blockchain_test_engine" onto "blockchain_test" so both formats' ids match (also covers *_from_state_test).
    private static string Key(string category, string name)
        => (category ?? string.Empty) + "::" + name.Replace("blockchain_test_engine", "blockchain_test");

    // Splits an engine fixture key into (category, name) the same way JsonToEthereumTest.GetNameAndCategory does for
    // the RLP side: everything before ".py::" is the category, everything after is the name.
    private static (string Category, string Name) SplitKey(string key)
    {
        key = key.Replace('\\', '/');
        int i = key.IndexOf(".py::", StringComparison.Ordinal);
        return i < 0 ? (string.Empty, key) : (key[..i], key[(i + 5)..]);
    }

    private static IReadOnlyDictionary<string, MutatedInfo> Build()
    {
        Dictionary<string, MutatedInfo> index = [];

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
