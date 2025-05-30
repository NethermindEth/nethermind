// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Nethermind.Specs.Test;

public class TestSpecHelper
{
    public static (ChainSpecBasedSpecProvider, ChainSpec) LoadChainSpec(ChainSpecJson spec)
    {
        EthereumJsonSerializer serializer = new();

        spec.Engine ??= new ChainSpecJson.EngineJson
        {
            CustomEngineData = new Dictionary<string, JsonElement> { { "NethDev", serializer.Deserialize<JsonElement>("{}") } }
        };

        ChainSpecLoader loader = new(serializer);
        MemoryStream data = new(Encoding.UTF8.GetBytes(serializer.Serialize(spec)));

        ChainSpec chainSpec = loader.Load(data);
        var x = new ChainSpecBasedSpecProvider(chainSpec);

        return (x, chainSpec);
    }
}
