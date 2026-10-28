// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Eez.Attester;

internal static class ChainConfigLoader
{
    public static ISpecProvider Load(string path)
    {
        (ISpecProvider specs, EezChainRules rules) = LoadProfile(path);
        if (rules.IsRollupX) throw new InvalidDataException($"{path} configures a RollupX chain, which requires the native streaming profile");
        return specs;
    }

    internal static (ISpecProvider Specs, EezChainRules Rules) LoadProfile(string path)
    {
        JsonNode document = Read(path);
        if (document["config"]?["rollupX"] is JsonObject rollup &&
            (rollup.ContainsKey("feeCollector") || rollup.ContainsKey("historyUpdateOffset")))
        {
            NativeGenesisSnapshot native = NativeGenesisSnapshot.Load(File.ReadAllBytes(path), NativeSystemTxFormat.ContractV6);
            return (native.Specs, native.Rules);
        }
        EezChainRules rules = EezChainRules.FromGenesis(JsonSerializer.SerializeToElement(document));
        return (new EezSpecProvider(new ChainSpecBasedSpecProvider(Parse(document)), rules), rules);
    }

    internal static ISpecProvider LoadL1(string path) => new ChainSpecBasedSpecProvider(Parse(Read(path)));

    private static JsonNode Read(string path)
    {
        JsonNode document = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is empty");
        if (document["config"] is null)
        {
            document = new JsonObject
            {
                ["config"] = document,
                ["alloc"] = new JsonObject(),
                ["gasLimit"] = "0x1c9c380",
                ["difficulty"] = "0x0",
                ["nonce"] = "0x0",
                ["timestamp"] = "0x0",
                ["extraData"] = "0x",
                ["mixHash"] = "0x0000000000000000000000000000000000000000000000000000000000000000",
                ["coinbase"] = "0x0000000000000000000000000000000000000000",
            };
        }

        return document;
    }

    private static ChainSpec Parse(JsonNode document) =>
        new GethGenesisLoader(new EthereumJsonSerializer()).Load(new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString())));
}
