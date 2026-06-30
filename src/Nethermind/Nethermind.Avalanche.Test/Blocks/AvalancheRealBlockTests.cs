// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Nethermind.Avalanche.Blocks;
using Nethermind.Avalanche.Sync;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Blocks;

/// <summary>
/// Validates the header codec against real Avalanche C-Chain <b>mainnet</b> blocks captured from the public
/// <c>api.avax.network</c> RPC, one per fork era. For each fixture the header is rebuilt from its JSON fields via
/// <see cref="AvalancheRpcHeaderMapper"/> and hashed; <see cref="AvalancheHeaderDecoder.ComputeHash"/> must
/// reproduce the block hash the network agreed on (<c>keccak256(RLP(header))</c>).
/// </summary>
/// <remarks>
/// Fixtures (<c>Blocks/RealBlocks/*.json</c>, embedded): Apricot Phase 4 (block 5,000,000, 19-item header),
/// Cancun/Etna (70,000,000, 22-item), Granite/ACP-226 (89,117,142, 24-item). The <c>eth_getBlockByNumber</c>
/// response omits header optionals that are absent from the RLP, so faithful presence-mapping reconstructs the
/// exact wire header across eras — covering both the "trailing optionals absent" (AP4) and "forced-zero middle
/// optionals" (Granite) paths of the <c>rlp:"optional"</c> cascade.
/// </remarks>
public class AvalancheRealBlockTests
{
    private static IEnumerable<TestCaseData> Fixtures()
    {
        Assembly assembly = typeof(AvalancheRealBlockTests).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (name.Contains("realblock", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.Ordinal))
            {
                yield return new TestCaseData(name).SetName($"RealBlock_{ShortName(name)}");
            }
        }
    }

    [TestCaseSource(nameof(Fixtures))]
    public void ComputeHash_matches_real_mainnet_block(string resourceName)
    {
        using JsonDocument doc = ReadResource(resourceName);
        JsonElement root = doc.RootElement;

        AvalancheBlockHeader header = AvalancheRpcHeaderMapper.MapHeader(root);
        Hash256 expected = AvalancheRpcHeaderMapper.ReportedHash(root);

        Hash256 computed = AvalancheHeaderDecoder.Instance.ComputeHash(header);
        Assert.That(computed, Is.EqualTo(expected),
            $"block {root.GetProperty("number").GetString()} ({resourceName})");

        // Encode -> decode -> hash must still match, proving the positional decode + optional-cascade re-encode
        // is byte-stable through a full round trip on real data.
        AvalancheBlockHeader decoded = AvalancheHeaderDecoder.Instance.Decode(
            AvalancheHeaderDecoder.Instance.Encode(header))!;
        Assert.That(decoded.Hash, Is.EqualTo(expected), "round-tripped hash");
    }

    [Test]
    public void All_three_era_fixtures_are_embedded()
    {
        int count = 0;
        foreach (TestCaseData _ in Fixtures()) count++;
        Assert.That(count, Is.EqualTo(3), "expected AP4 + Cancun + Granite real-block fixtures");
    }

    private static string ShortName(string resource)
    {
        int i = resource.IndexOf("realblock", StringComparison.OrdinalIgnoreCase);
        string tail = i >= 0 ? resource[i..] : resource;
        return tail.EndsWith(".json", StringComparison.Ordinal) ? tail[..^5] : tail;
    }

    private static JsonDocument ReadResource(string name)
    {
        using Stream stream = typeof(AvalancheRealBlockTests).Assembly.GetManifestResourceStream(name)
                              ?? throw new InvalidOperationException($"embedded resource not found: {name}");
        using StreamReader reader = new(stream);
        return JsonDocument.Parse(reader.ReadToEnd());
    }
}
