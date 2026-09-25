// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using System.Text.Json;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

/// <summary>Loads the recorded EEZ blocks, witnesses and chain configurations under <c>Fixtures</c>.</summary>
internal static class StatelessFixtures
{
    public static string PathOf(string fixture, string file) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fixture, file);

    public static byte[] ReadBlock(string fixture, string file) =>
        file.EndsWith(".hex") ? Bytes.FromHexString(File.ReadAllText(PathOf(fixture, file)).Trim()) : File.ReadAllBytes(PathOf(fixture, file));

    public static EezStatelessBlock ReadBlock(string fixture, string blockFile, string witnessFile) =>
        new(ReadBlock(fixture, blockFile), ReadWitness(fixture, witnessFile));

    public static Witness ReadWitness(string fixture, string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(fixture, file)));
        return new Witness
        {
            State = ReadHexList(json.RootElement, "state"),
            Codes = ReadHexList(json.RootElement, "codes"),
            Keys = ReadHexList(json.RootElement, "keys"),
            Headers = ReadHexList(json.RootElement, "headers"),
        };
    }

    public static JsonElement ReadJson(string fixture, string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(fixture, file)));
        return json.RootElement.Clone();
    }

    /// <summary>Builds the spec of a bare chain configuration, the only part of a genesis stateless execution needs.</summary>
    public static ISpecProvider ReadSpecProvider(string fixture, string file)
    {
        string genesis = $$"""
            {"config":{{File.ReadAllText(PathOf(fixture, file))}},"alloc":{},"gasLimit":"0x1c9c380","difficulty":"0x0",
             "nonce":"0x0","timestamp":"0x0","extraData":"0x","mixHash":"0x0000000000000000000000000000000000000000000000000000000000000000",
             "coinbase":"0x0000000000000000000000000000000000000000"}
            """;
        ChainSpec chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(new MemoryStream(Encoding.UTF8.GetBytes(genesis)));
        return new EezSpecProvider(new ChainSpecBasedSpecProvider(chainSpec));
    }

    private static ArrayPoolList<byte[]> ReadHexList(JsonElement root, string property)
    {
        JsonElement items = root.GetProperty(property);
        ArrayPoolList<byte[]> list = new(items.GetArrayLength());
        foreach (JsonElement item in items.EnumerateArray())
        {
            list.Add(Bytes.FromHexString(item.GetString()!));
        }

        return list;
    }
}
