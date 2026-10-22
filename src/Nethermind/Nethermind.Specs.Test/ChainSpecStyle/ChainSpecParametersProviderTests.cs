// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[TestFixture]
public class ChainSpecParametersProviderTests
{
    [TestCase("Ethash")]
    [TestCase("eThAsH")]
    public void Accepts_engine_sections_with_a_loaded_owner_case_insensitively(string engineName)
    {
        Dictionary<string, JsonElement> engineParameters = new()
        {
            [engineName] = JsonSerializer.Deserialize<JsonElement>("{}")
        };

        ChainSpecParametersProvider provider = new(engineParameters, new EthereumJsonSerializer());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.SealEngineType, Is.EqualTo(SealEngineType.Ethash));
            Assert.That(provider.GetChainSpecParameters<EthashChainSpecEngineParameters>(), Is.Not.Null);
        }
    }

    [Test]
    public void Rejects_engine_sections_without_a_loaded_owner()
    {
        Dictionary<string, JsonElement> engineParameters = new()
        {
            ["eThAsH"] = JsonSerializer.Deserialize<JsonElement>("{}"),
            ["FakeEngine"] = JsonSerializer.Deserialize<JsonElement>("{}")
        };

        Assert.That(
            () => new ChainSpecParametersProvider(engineParameters, new EthereumJsonSerializer()),
            Throws.InvalidOperationException.With.Message.Contains("FakeEngine"));
    }
}
