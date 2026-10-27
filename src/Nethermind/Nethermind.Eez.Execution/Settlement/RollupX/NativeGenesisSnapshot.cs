// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The native execution profile and commitments derived from one complete genesis document.</summary>
/// <remarks>The configured L1 reference is not authenticated until request ancestry or canonical L1 is checked.</remarks>
internal sealed class NativeGenesisSnapshot
{
    internal EezChainRules Rules { get; }
    internal NativeHistorySchedule History { get; }
    internal ISpecProvider Specs { get; }
    internal ValueHash256 GenesisHash { get; }
    internal ValueHash256 SourceDigest { get; }

    private NativeGenesisSnapshot(EezChainRules rules, NativeHistorySchedule history, ChainSpec spec, in ValueHash256 digest)
    {
        Rules = rules;
        History = history;
        GenesisHash = EezGenesisHash.Compute(spec).ValueHash256;
        SourceDigest = digest;
        Specs = new EezSpecProvider(new ChainSpecBasedSpecProvider(spec), rules);
    }

    internal static NativeGenesisSnapshot Load(ReadOnlyMemory<byte> input)
    {
        try
        {
            ReadOnlyMemory<byte> canonical = RollupXCanonicalJson.Encode(input.ToArray());
            JsonObject document = JsonNode.Parse(canonical.Span)?.AsObject() ?? throw new InvalidDataException("Missing native genesis document.");
            NormalizeMixHash(document);
            byte[] normalized = JsonSerializer.SerializeToUtf8Bytes(document);
            using JsonDocument json = JsonDocument.Parse(normalized);
            (EezChainRules rules, NativeHistorySchedule history) = RollupXGenesis.ReadNative(json.RootElement);
            using MemoryStream stream = new(normalized, writable: false);
            ChainSpec spec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(stream);
            BlockHeader header = spec.Genesis!.Header;
            string? error = RollupXHeaderValidator.ProfileMismatch(header, rules.Schedule!);
            if (error is not null || header.ExtraData.Length > 32)
                throw new InvalidDataException($"Invalid native genesis header: {error ?? "extra data exceeds 32 bytes"}.");
            ValueHash256 digest = ValueKeccak.Compute(canonical.Span);
            return new NativeGenesisSnapshot(rules, history, spec, in digest);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid native genesis document.", exception);
        }
    }

    private static void NormalizeMixHash(JsonObject document)
    {
        if (!document.TryGetPropertyValue("mixhash", out JsonNode? alias)) return;
        document.Remove("mixhash");
        if (document.TryGetPropertyValue("mixHash", out JsonNode? value))
        {
            if (!JsonNode.DeepEquals(value, alias)) throw new InvalidDataException("Genesis mixHash and mixhash disagree.");
        }
        else document["mixHash"] = alias;
    }
}
