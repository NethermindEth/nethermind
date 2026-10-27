// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution;

public static partial class RollupXGenesis
{
    internal static (EezChainRules Rules, NativeHistorySchedule History) ReadNative(JsonElement genesis)
    {
        EnsureNativeDocument(genesis);
        JsonElement config = Required(genesis, "config");
        JsonElement section = Required(config, SectionName);
        EnsureOnly(section, "blockIntervalSeconds", "syncIntervalBlocks", "l1Reference", "historyUpdateOffset", "feeCollector");
        RollupXSchedule schedule = ReadScheduleValues(section, Quantity(genesis, "timestamp"));
        EnsureSchedule(schedule, Hash(genesis, "mixHash"));
        EnsureForks(config);
        EnsureBaseFee(genesis);
        EnsureHistoryPredeploy(genesis);
        ulong offset = Number(section, "historyUpdateOffset");
        if (offset > uint.MaxValue || !Address.TryParse(Required(section, "feeCollector").GetString(), out Address? collector) || collector is null)
            throw Invalid("invalid history update offset or fee collector");
        NativeHistorySchedule history = new(schedule.GenesisTimestamp, schedule.BlockIntervalSeconds, schedule.SyncIntervalBlocks, (uint)offset);
        if (schedule.L1Reference.Timestamp > history.BootstrapCutoff)
            throw Invalid("the L1 reference is after the history bootstrap cutoff");
        EezChainRules rules = EezChainRules.NativeRollupX(schedule, collector);
        EnsureNativeHistory(genesis, schedule, history);
        return (rules, history);
    }

    private static void EnsureNativeDocument(JsonElement genesis)
    {
        EnsureOnly(genesis, "alloc", "baseFeePerGas", "blobGasUsed", "coinbase", "config", "difficulty", "excessBlobGas",
            "extraData", "gasLimit", "mixHash", "nonce", "number", "parentHash", "timestamp");
        if ((genesis.TryGetProperty("number", out JsonElement number) && ParseQuantity(number, "number") != 0) ||
            (genesis.TryGetProperty("parentHash", out _) && Hash(genesis, "parentHash") != Keccak.Zero))
            throw Invalid("genesis number and parent hash must be zero");
        JsonElement config = Required(genesis, "config");
        EnsureOnly(config, "chainId", "homesteadBlock", "daoForkBlock", "daoForkSupport", "eip150Block", "eip155Block",
            "eip158Block", "byzantiumBlock", "constantinopleBlock", "petersburgBlock", "istanbulBlock", "muirGlacierBlock", "berlinBlock",
            "londonBlock", "arrowGlacierBlock", "grayGlacierBlock", "mergeNetsplitBlock", "terminalTotalDifficulty",
            "terminalTotalDifficultyPassed", "shanghaiTime", "cancunTime", "pragueTime", "osakaTime", "amsterdamTime", "bogotaTime",
            "bpo1Time", "bpo2Time", "bpo3Time", "bpo4Time", "bpo5Time", "depositContractAddress", "blobSchedule", SectionName);
        if (Quantity(config, "chainId") == 0) throw Invalid("missing L2 chain id");
        if (config.TryGetProperty("daoForkSupport", out JsonElement dao) && dao.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("daoForkSupport must be a boolean");
        if (config.TryGetProperty("blobSchedule", out JsonElement blobs) && blobs.ValueKind != JsonValueKind.Null)
        {
            EnsureOnly(blobs, "cancun", "prague", "osaka");
            foreach (JsonProperty fork in blobs.EnumerateObject())
                EnsureOnly(fork.Value, "target", "max", "baseFeeUpdateFraction");
        }
        JsonElement allocations = Required(genesis, "alloc");
        if (allocations.ValueKind != JsonValueKind.Object) throw Invalid("alloc must be an object");
        HashSet<Address> addresses = new(allocations.GetPropertyCount());
        foreach (JsonProperty entry in allocations.EnumerateObject())
        {
            string name = entry.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? entry.Name : "0x" + entry.Name;
            if (!Address.TryParse(name, out Address? address) || address is null || !addresses.Add(address)) throw Invalid("invalid or duplicate account address");
            EnsureOnly(entry.Value, "balance", "nonce", "code", "storage");
            if (entry.Value.TryGetProperty("storage", out JsonElement storage) && storage.ValueKind != JsonValueKind.Null)
            {
                if (storage.ValueKind != JsonValueKind.Object) throw Invalid("storage must be an object");
                HashSet<UInt256> keys = new(storage.GetPropertyCount());
                foreach (JsonProperty slot in storage.EnumerateObject())
                {
                    if (!keys.Add(StorageWord(slot.Name))) throw Invalid("duplicate storage slot");
                    _ = StorageWord(slot.Value.GetString()!);
                }
            }
        }
    }

    private static void EnsureNativeHistory(JsonElement genesis, RollupXSchedule schedule, NativeHistorySchedule history)
    {
        JsonElement account = FindAccount(genesis, EezConstants.L1HistoryAddress) ?? throw Invalid("missing native history predeploy");
        if (Quantity(account, "nonce") != 1 || Quantity(account, "balance") != 0 ||
            ValueKeccak.Compute(Bytes.FromHexString(Required(account, "code").GetString()!)) != NativeHistoryBlockExecutor.RuntimeHash)
            throw Invalid("native history code, nonce or balance differs from the profile");
        JsonElement storage = Required(account, "storage");
        Dictionary<UInt256, UInt256> expected = new(7)
        {
            [(UInt256)1] = schedule.L1Reference.Number,
            [(UInt256)2] = schedule.L1Reference.Number,
            [(UInt256)3] = schedule.GenesisTimestamp,
            [(UInt256)4] = history.Interval,
            [(UInt256)5] = history.Delay,
            [(UInt256)6] = NativeHistoryCall.MaxHashes,
            [NativeHistoryBlockExecutor.HashSlot(schedule.L1Reference.Number)] = new UInt256(schedule.L1Reference.Hash.Bytes, isBigEndian: true)
        };
        if (storage.ValueKind != JsonValueKind.Object || storage.GetPropertyCount() != expected.Count)
            throw Invalid("native history genesis storage differs from the profile");
        foreach (JsonProperty entry in storage.EnumerateObject())
        {
            UInt256 key = StorageWord(entry.Name);
            UInt256 value = StorageWord(entry.Value.GetString()!);
            if (!expected.Remove(key, out UInt256 wanted) || value != wanted)
                throw Invalid("native history genesis storage differs from the profile");
        }
    }

    private static UInt256 StorageWord(string? text)
    {
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) throw Invalid("storage words must be hex values");
        byte[] bytes = Bytes.FromHexString(text);
        if (bytes.Length > AbiWord.Size) throw Invalid("storage words exceed 256 bits");
        return new UInt256(bytes, isBigEndian: true);
    }
}
