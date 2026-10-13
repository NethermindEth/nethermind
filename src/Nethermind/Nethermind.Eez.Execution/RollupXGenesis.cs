// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Eez.Execution;

/// <summary>
/// Reads and checks the <c>config.rollupX</c> section of an Ethereum-style genesis, which makes the chain a RollupX
/// chain. The section has no defaults and no unknown keys; the genesis must activate every fork through Osaka at
/// zero and nothing later, sit on the L1 slot grid of its reference, carry the reference's RANDAO, start at a
/// 1 gwei base fee and hold the EIP-2935 history predeploy.
/// </summary>
public static class RollupXGenesis
{
    public const string SectionName = "rollupX";

    private static readonly byte[] HistoryStorageCode = Bytes.FromHexString(
        "0x3373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500");

    private static readonly string[] ForksAtGenesis =
    [
        "homesteadBlock", "eip150Block", "eip155Block", "eip158Block", "byzantiumBlock", "constantinopleBlock", "petersburgBlock",
        "istanbulBlock", "berlinBlock", "londonBlock", "mergeNetsplitBlock", "shanghaiTime", "cancunTime", "pragueTime", "osakaTime",
    ];

    private static readonly string[] UnsupportedForks = ["amsterdamTime", "bogotaTime", "daoForkBlock"];

    /// <summary>The schedule of a RollupX genesis, or <c>null</c> for a genesis without a <c>rollupX</c> section.</summary>
    /// <exception cref="InvalidDataException">The section or the genesis breaks a RollupX rule.</exception>
    public static RollupXSchedule? Read(JsonElement genesis)
    {
        if (!genesis.TryGetProperty("config", out JsonElement config) || !config.TryGetProperty(SectionName, out JsonElement section))
        {
            return null;
        }

        RollupXSchedule schedule = ReadSchedule(section, Quantity(genesis, "timestamp"));
        EnsureSchedule(schedule, Hash(genesis, "mixHash"));
        EnsureForks(config);
        EnsureBaseFee(genesis);
        EnsureHistoryPredeploy(genesis);
        return schedule;
    }

    private static RollupXSchedule ReadSchedule(JsonElement section, ulong genesisTimestamp)
    {
        EnsureOnly(section, "blockIntervalSeconds", "syncIntervalBlocks", "l1Reference");
        JsonElement reference = Required(section, "l1Reference");
        EnsureOnly(reference, "chainId", "number", "hash", "timestamp", "prevRandao");
        ulong syncInterval = Number(section, "syncIntervalBlocks");
        return new RollupXSchedule(
            Number(section, "blockIntervalSeconds"),
            syncInterval <= uint.MaxValue ? (uint)syncInterval : throw Invalid("syncIntervalBlocks exceeds 32 bits"),
            new RollupXL1Reference(Number(reference, "chainId"), Number(reference, "number"), Hash(reference, "hash"),
                Number(reference, "timestamp"), Hash(reference, "prevRandao")),
            genesisTimestamp);
    }

    private static void EnsureSchedule(RollupXSchedule schedule, Hash256 genesisMixHash)
    {
        RollupXL1Reference reference = schedule.L1Reference;
        if (schedule.BlockIntervalSeconds == 0 || schedule.SyncIntervalBlocks < 2 || reference.ChainId == 0 || reference.Hash == Keccak.Zero)
        {
            throw Invalid("blockIntervalSeconds, syncIntervalBlocks, the L1 chain id and the L1 hash must be set");
        }

        (ulong Interval, uint Positions)? required = reference.ChainId switch
        {
            RollupXSchedule.EthereumChainId => (2, 6),
            RollupXSchedule.ChiadoChainId => (1, 5),
            _ => null,
        };
        if (required is { } r && (schedule.BlockIntervalSeconds, schedule.SyncIntervalBlocks) != r)
        {
            throw Invalid($"L1 chain {reference.ChainId} requires {r.Interval}-second blocks and {r.Positions} positions per slot");
        }

        if (schedule.BlockIntervalSeconds > ulong.MaxValue / schedule.SyncIntervalBlocks)
        {
            throw Invalid("the L1 slot length overflows");
        }

        if (schedule.GenesisTimestamp < reference.Timestamp || (schedule.GenesisTimestamp - reference.Timestamp) % schedule.L1SlotSeconds != 0)
        {
            throw Invalid("the genesis timestamp is not on the L1 slot grid of its reference");
        }

        if (genesisMixHash != reference.PrevRandao)
        {
            throw Invalid("the genesis mixHash is not the L1 reference's prevRandao");
        }
    }

    private static void EnsureForks(JsonElement config)
    {
        foreach (string fork in ForksAtGenesis)
        {
            if (!config.TryGetProperty(fork, out JsonElement activation) || ParseQuantity(activation, fork) != 0)
            {
                throw Invalid($"{fork} must be 0");
            }
        }

        foreach (string fork in UnsupportedForks)
        {
            if (config.TryGetProperty(fork, out JsonElement activation) && activation.ValueKind != JsonValueKind.Null)
            {
                throw Invalid($"{fork} is not supported");
            }
        }

        if (!config.TryGetProperty("terminalTotalDifficulty", out JsonElement ttd) || ParseQuantity(ttd, "terminalTotalDifficulty") != 0
            || !config.TryGetProperty("terminalTotalDifficultyPassed", out JsonElement passed) || passed.ValueKind != JsonValueKind.True)
        {
            throw Invalid("the terminal total difficulty must be 0 and passed");
        }
    }

    private static void EnsureBaseFee(JsonElement genesis)
    {
        if (genesis.TryGetProperty("baseFeePerGas", out JsonElement baseFee) && baseFee.ValueKind != JsonValueKind.Null
            && ParseQuantity(baseFee, "baseFeePerGas") != Eip1559Constants.DefaultForkBaseFee)
        {
            throw Invalid("the genesis base fee must be 1 gwei");
        }
    }

    private static void EnsureHistoryPredeploy(JsonElement genesis)
    {
        JsonElement? history = FindAccount(genesis, Eip2935Constants.BlockHashHistoryAddress);
        bool holdsCode = history is { } account && account.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String
            && Bytes.FromHexString(code.GetString()!).AsSpan().SequenceEqual(HistoryStorageCode);
        bool storageEmpty = history is not { } holder || !holder.TryGetProperty("storage", out JsonElement storage)
            || storage.ValueKind == JsonValueKind.Null || !storage.EnumerateObject().MoveNext();
        if (!holdsCode || !storageEmpty)
        {
            throw Invalid($"the genesis must hold the EIP-2935 history predeploy at {Eip2935Constants.BlockHashHistoryAddress} with its code and no storage");
        }
    }

    private static JsonElement? FindAccount(JsonElement genesis, Address address)
    {
        if (!genesis.TryGetProperty("alloc", out JsonElement alloc) || alloc.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty account in alloc.EnumerateObject())
        {
            string name = account.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? account.Name : "0x" + account.Name;
            if (Address.TryParse(name, out Address? parsed) && parsed == address)
            {
                return account.Value;
            }
        }

        return null;
    }

    private static void EnsureOnly(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("expected an object");
        }

        HashSet<string> allowed = [.. names];
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid($"unknown key {property.Name}");
            }
        }
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : throw Invalid($"{name} is required");

    private static ulong Number(JsonElement element, string name)
    {
        JsonElement value = Required(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number) ? number : throw Invalid($"{name} must be an unsigned integer");
    }

    private static ulong Quantity(JsonElement element, string name) => ParseQuantity(Required(element, name), name);

    private static ulong ParseQuantity(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number))
        {
            return number;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is not null && (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)
                : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            ? number
            : throw Invalid($"{name} must be a quantity");
    }

    private static Hash256 Hash(JsonElement element, string name)
    {
        string? text = Required(element, name).GetString();
        byte[] bytes = text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Bytes.FromHexString(text) : [];
        return bytes.Length == Hash256.Size ? new Hash256(bytes) : throw Invalid($"{name} must be a 32-byte hex value");
    }

    private static InvalidDataException Invalid(string reason) => new($"Invalid RollupX genesis: {reason}.");
}
