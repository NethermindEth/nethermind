// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Nethermind.Avalanche.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Blocks;

/// <summary>
/// Validates the header codec against real Avalanche C-Chain <b>mainnet</b> blocks captured from the public
/// <c>api.avax.network</c> RPC, one per fork era. For each fixture the header is rebuilt from its JSON fields and
/// hashed; <see cref="AvalancheHeaderDecoder.ComputeHash"/> must reproduce the block hash the network agreed on
/// (<c>keccak256(RLP(header))</c>).
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

        AvalancheBlockHeader header = BuildHeader(root);
        Hash256 expected = new(GetString(root, "hash"));

        Hash256 computed = AvalancheHeaderDecoder.Instance.ComputeHash(header);
        Assert.That(computed, Is.EqualTo(expected), $"block {GetString(root, "number")} ({resourceName})");

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

    private static AvalancheBlockHeader BuildHeader(JsonElement b)
    {
        AvalancheBlockHeader header = new(
            new Hash256(GetString(b, "parentHash")),
            new Hash256(GetString(b, "sha3Uncles")),
            new Address(GetString(b, "miner")),
            difficulty: (UInt256)ToULong(GetString(b, "difficulty")),
            number: ToULong(GetString(b, "number")),
            gasLimit: ToULong(GetString(b, "gasLimit")),
            timestamp: ToULong(GetString(b, "timestamp")),
            extraData: Bytes.FromHexString(GetString(b, "extraData")))
        {
            StateRoot = new Hash256(GetString(b, "stateRoot")),
            TxRoot = new Hash256(GetString(b, "transactionsRoot")),
            ReceiptsRoot = new Hash256(GetString(b, "receiptsRoot")),
            Bloom = new Bloom(Bytes.FromHexString(GetString(b, "logsBloom"))),
            GasUsed = ToULong(GetString(b, "gasUsed")),
            MixHash = new Hash256(GetString(b, "mixHash")),
            Nonce = ToULong(GetString(b, "nonce")),
            ExtDataHash = new Hash256(GetString(b, "extDataHash"))
        };

        // Trailing rlp:"optional" fields: present in the JSON iff present in the wire RLP.
        if (TryGetString(b, "baseFeePerGas", out string? baseFee)) header.BaseFeePerGas = (UInt256)ToULong(baseFee!);
        if (TryGetString(b, "extDataGasUsed", out string? extGas)) header.ExtDataGasUsed = (UInt256)ToULong(extGas!);
        if (TryGetString(b, "blockGasCost", out string? blockGasCost)) header.BlockGasCost = (UInt256)ToULong(blockGasCost!);
        if (TryGetString(b, "blobGasUsed", out string? blobGasUsed)) header.BlobGasUsed = ToULong(blobGasUsed!);
        if (TryGetString(b, "excessBlobGas", out string? excessBlobGas)) header.ExcessBlobGas = ToULong(excessBlobGas!);
        if (TryGetString(b, "parentBeaconBlockRoot", out string? beacon)) header.ParentBeaconBlockRoot = new Hash256(beacon!);
        if (TryGetString(b, "timestampMilliseconds", out string? timeMs)) header.TimeMilliseconds = ToULong(timeMs!);
        if (TryGetString(b, "minDelayExcess", out string? minDelay)) header.MinDelayExcess = ToULong(minDelay!);

        return header;
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

    private static string GetString(JsonElement obj, string name) =>
        obj.GetProperty(name).GetString() ?? throw new InvalidOperationException($"missing field {name}");

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        if (obj.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static ulong ToULong(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.StartsWith("0x")) span = span[2..];
        return span.IsEmpty ? 0UL : Convert.ToUInt64(span.ToString(), 16);
    }
}
