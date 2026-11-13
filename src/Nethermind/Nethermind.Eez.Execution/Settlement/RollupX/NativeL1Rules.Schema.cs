// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeL1Rules
{
    private static readonly string[] ConfigFields =
    [
        "chainId", "homesteadBlock", "daoForkBlock", "daoForkSupport", "eip150Block", "eip155Block", "eip158Block", "byzantiumBlock",
        "constantinopleBlock", "petersburgBlock", "istanbulBlock", "muirGlacierBlock", "berlinBlock", "londonBlock", "arrowGlacierBlock",
        "grayGlacierBlock", "mergeNetsplitBlock", "terminalTotalDifficulty", "terminalTotalDifficultyPassed", "shanghaiTime", "cancunTime",
        "pragueTime", "osakaTime", "amsterdamTime", "bogotaTime", "bpo1Time", "bpo2Time", "bpo3Time", "bpo4Time", "bpo5Time",
        "depositContractAddress", "blobSchedule", "ethash", "clique", "parlia"
    ];
    private static readonly string[] BlobKeys = ["cancun", "prague", "osaka", "bpo1", "bpo2", "bpo3", "bpo4", "bpo5", "Amsterdam"];
    private static readonly string[] BlobFields = ["target", "max", "baseFeeUpdateFraction"];

    private static void ValidateDocument(JsonElement genesis)
    {
        NativeChainDocument.ValidateGenesis(genesis);
        JsonElement config = NativeChainDocument.Required(genesis, "config");
        NativeChainDocument.EnsureFields(config, ConfigFields);
        _ = Number(NativeChainDocument.Required(config, "chainId"));
        foreach (string name in new[] { "daoForkSupport", "terminalTotalDifficultyPassed" })
            if (config.TryGetProperty(name, out JsonElement flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid($"{name} must be a boolean");
        foreach (string name in new[] { "ethash", "clique", "parlia" })
        {
            if (!config.TryGetProperty(name, out JsonElement engine) || engine.ValueKind == JsonValueKind.Null) continue;
            NativeChainDocument.EnsureFields(engine, name == "ethash" ? [] : ["period", "epoch"]);
            foreach (JsonProperty value in engine.EnumerateObject()) _ = NativeChainDocument.Quantity(value.Value);
        }
        if (!config.TryGetProperty("blobSchedule", out JsonElement blobs)) return;
        NativeChainDocument.EnsureFields(blobs, BlobKeys);
        foreach (JsonProperty fork in blobs.EnumerateObject())
        {
            NativeChainDocument.EnsureFields(fork.Value, BlobFields);
            ulong target = Number(NativeChainDocument.Required(fork.Value, "target"));
            ulong max = Number(NativeChainDocument.Required(fork.Value, "max"));
            ulong fraction = Number(NativeChainDocument.Required(fork.Value, "baseFeeUpdateFraction"));
            if (target > max || max == 0 || fraction == 0) throw Invalid("invalid blob parameters");
        }
    }

    private static ulong Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)
        ? number : throw Invalid("expected an unsigned integer");
}
