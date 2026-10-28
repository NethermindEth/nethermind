// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution;

public static partial class RollupXGenesis
{
    internal static (EezChainRules Rules, NativeHistorySchedule History) ReadNative(JsonElement genesis, NativeSystemTxFormat format = NativeSystemTxFormat.RecordedV1)
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
        EezChainRules rules = EezChainRules.NativeRollupX(schedule, collector, format, (uint)offset);
        EnsureNativeHistory(genesis, schedule, history, format);
        return (rules, history);
    }

    private static void EnsureNativeDocument(JsonElement genesis)
    {
        NativeChainDocument.ValidateGenesis(genesis);
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
    }

    private static void EnsureNativeHistory(JsonElement genesis, RollupXSchedule schedule, NativeHistorySchedule history, NativeSystemTxFormat format)
    {
        if (format.UsesProxyHistory())
        {
            EnsureNativeProxyHistory(genesis);
            return;
        }
        JsonElement account = NativeHistoryAccount(genesis, EezConstants.L1HistoryAddress, 1, NativeHistoryBlockExecutor.GetRuntimeHash(format));
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
        RequireNativeHistoryStorage(account, expected);
    }

}
