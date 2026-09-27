// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class McpReadmeExamplesTests
{
    private McpTestNode _node = null!;
    private McpClient _client = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _node = await McpTestNode.Create(configureContainer: builder => builder
            .AddSingleton<TimeProvider>(new ExampleTimeProvider())
            .AddScoped<IGenesisPostProcessor, ExampleFeed>());
        for (int i = 0; i < 101; i++) await _node.Chain.AddBlock();
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [TestCase("diagnose_transaction")]
    [TestCase("address_activity")]
    [TestCase("token_price")]
    public async Task Readme_example_matches_actual_output_and_schema(string tool)
    {
        using Stream stream = typeof(McpReadmeExamplesTests).Assembly.GetManifestResourceStream("McpReadme")!;
        using StreamReader reader = new(stream);
        string[] lines = (await reader.ReadToEndAsync()).Split('\n');
        int index = Array.FindIndex(lines, line => line.StartsWith("{\"name\":\"" + tool + "\"", StringComparison.Ordinal));
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        using JsonDocument request = JsonDocument.Parse(lines[index]);
        using JsonDocument expected = JsonDocument.Parse(lines[index + 1]);
        (string, object?)[] args = request.RootElement.GetProperty("arguments").EnumerateObject()
            .Select(static property => (property.Name, (object?)property.Value.Clone())).ToArray();

        CallToolResult call = await McpToolCalls.Call(_client, tool, args);

        McpAssert.Success(call);
        Assert.That(JsonElement.DeepEquals(call.StructuredContent!.Value, expected.RootElement), Is.True,
            () => $"README: {expected.RootElement}\nActual: {call.StructuredContent.Value}");
        await McpToolCalls.AssertConformsToOutputSchema(_client, tool, call);
    }

    private sealed class ExampleTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_700_000_030);
    }

    private sealed class ExampleFeed(IWorldState state, ISpecProvider specs) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis)
        {
            Address feed = new("0x5f4eC3Df9cbd43714FE2740f5E3616155c5b8419");
            state.CreateAccount(feed, UInt256.Zero);
            state.InsertCode(feed, TestContracts.Aggregator(350_000_000_000, 1_700_000_000, 1, 1), specs.GenesisSpec, isGenesis: true);
        }
    }
}
