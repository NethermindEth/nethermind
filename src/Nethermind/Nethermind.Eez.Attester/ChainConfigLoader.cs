// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Eez.Attester;

/// <summary>Loads the L2 fork schedule from a genesis document, or from its bare chain configuration.</summary>
internal static class ChainConfigLoader
{
    public static ISpecProvider Load(string path)
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

        ChainSpec chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString())));
        return new EezSpecProvider(new ChainSpecBasedSpecProvider(chainSpec));
    }
}
