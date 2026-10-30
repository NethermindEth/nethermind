// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Locally derived effective L1 rules, source identity and offline manager profile.</summary>
internal sealed partial class NativeL1Rules
{
    private readonly ulong[] _ruleTimes;
    private readonly ulong _lastBlockFork;
    internal ISpecProvider Specs { get; }
    internal NativeL1HeaderRules Headers { get; }
    internal ValueHash256 GenesisHash { get; }
    internal ValueHash256 SourceDigest { get; }
    internal ValueHash256 ConfigHash { get; }
    internal ReadOnlySpan<ulong> RuleTimes => _ruleTimes;
    internal ulong MaximumHistoricalBlobCount { get; }

    private NativeL1Rules(ChainSpec spec, in ValueHash256 sourceDigest, GethGenesisConfigJson config)
    {
        SourceDigest = sourceDigest;
        Headers = new NativeL1HeaderRules(config.LondonBlock, config.ShanghaiTime, config.CancunTime, config.PragueTime,
            Earliest(config.AmsterdamTime, config.BogotaTime));
        BlockHeader genesis = spec.Genesis!.Header;
        if (genesis.BaseFeePerGas > ulong.MaxValue) throw Invalid("genesis base fee exceeds the supported L1 header range");
        if (config.LondonBlock is not 0) genesis.BaseFeePerGas = 0;
        GenesisHash = EezGenesisHash.Compute(spec).ValueHash256;
        _ = NativeL1Header.Decode(Rlp.Encode(genesis).Bytes, Headers);
        SortedSet<ulong> times = [0];
        JsonObject forks = Forks(config, times, out _lastBlockFork);
        JsonObject blobs = BlobRules(config, times, spec, out ulong maximum);
        MaximumHistoricalBlobCount = maximum;
        if (times.Count > NativeOfflineProfile.MaxRuleEpochs) throw Invalid("too many rule epochs");
        _ruleTimes = new ulong[times.Count];
        times.CopyTo(_ruleTimes);
        JsonObject document = new()
        {
            ["domain"] = "EEZ_ROLLUPX_EFFECTIVE_L1_RULES_V1",
            ["chainId"] = config.ChainId,
            ["genesisHash"] = GenesisHash.ToString(),
            ["forks"] = forks,
            ["headerRules"] = new JsonObject
            {
                ["londonBlock"] = Headers.LondonBlock,
                ["shanghaiTime"] = Headers.ShanghaiTime,
                ["cancunTime"] = Headers.CancunTime,
                ["pragueTime"] = Headers.PragueTime,
                ["unsupportedTime"] = Headers.UnsupportedTime
            },
            ["blobRules"] = blobs
        };
        ConfigHash = ValueKeccak.Compute(RollupXCanonicalJson.Encode(JsonSerializer.SerializeToUtf8Bytes(document)).Span);
        Specs = new ChainSpecBasedSpecProvider(spec);
    }

    internal static NativeL1Rules LoadEthereum(ReadOnlyMemory<byte> input)
    {
        try
        {
            (byte[] normalized, ValueHash256 digest) = NativeChainDocument.Read(input);
            using JsonDocument document = JsonDocument.Parse(normalized);
            ValidateDocument(document.RootElement);
            EthereumJsonSerializer serializer = new();
            GethGenesisConfigJson config = serializer.Deserialize<GethGenesisConfigJson>(document.RootElement.GetProperty("config").GetRawText())
                ?? throw Invalid("missing L1 config");
            if (config.ChainId is 0 or BlockchainIds.Gnosis or BlockchainIds.Chiado) throw Invalid("an Ethereum L1 profile cannot select a Gnosis network");
            using MemoryStream stream = new(normalized, writable: false);
            ChainSpec spec = new GethGenesisLoader(serializer).Load(stream);
            return new NativeL1Rules(spec, in digest, config);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or EezSettlementException)
        {
            throw new InvalidDataException("Invalid native L1 rules.", exception);
        }
    }

    internal NativeOfflineProfile.Inputs Profile(NativeGenesisSnapshot l2)
    {
        RollupXSchedule schedule = l2.Rules.Schedule!;
        ValidateBootstrap(schedule.L1Reference);
        ValueHash256 runtimeHash = NativeHistoryBlockExecutor.GetRuntimeHash(l2.Rules.NativeFormat);
        NativeOfflineProfile.Inputs result = new(Specs.ChainId, l2.Specs.ChainId, l2.GenesisHash, schedule.GenesisTimestamp,
            schedule.BlockIntervalSeconds, schedule.SyncIntervalBlocks, checked((uint)(l2.History.Delay / l2.History.BlockInterval - 1)),
            ConfigHash, runtimeHash, l2.Rules.FeeCollector!,
            l2.Rules.NativeFormat switch
            {
                NativeSystemTxFormat.ContractV7 => 7UL,
                NativeSystemTxFormat.ContractV6 => 6UL,
                NativeSystemTxFormat.ContractV4 => 4UL,
                _ => 3UL,
            });
        NativeOfflineProfile.Validate(in result, _ruleTimes, result.L1ChainId, runtimeHash);
        return result;
    }

    internal void ValidateBootstrap(RollupXL1Reference reference)
    {
        if (reference.ChainId != Specs.ChainId || reference.Number < _lastBlockFork)
            throw Invalid("L2 bootstrap differs from L1 chain or precedes a block-based fork");
    }

    internal ulong PostingBlobLimit(ulong? timestamp, ulong parentNumber)
    {
        if (timestamp is null) return MaximumHistoricalBlobCount;
        if (parentNumber == ulong.MaxValue || Headers.UnsupportedTime is { } unsupported && timestamp.Value >= unsupported) return 0;
        IReleaseSpec spec = Specs.GetSpec(parentNumber + 1, timestamp.Value);
        return spec.IsEip4844Enabled ? Math.Min(spec.MaxBlobCount, spec.MaxBlobsPerTx) : 0;
    }

    private static ulong? Earliest(ulong? left, ulong? right) => left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
    private static InvalidDataException Invalid(string reason) => new($"Invalid native L1 rules: {reason}.");
}
