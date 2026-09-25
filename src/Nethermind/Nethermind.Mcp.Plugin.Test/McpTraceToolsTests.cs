// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>End-to-end tests of <c>trace_transaction</c> on the <see cref="McpTxScenario"/> chain, with a small frame cap.</summary>
[Parallelizable(ParallelScope.Self)]
public class McpTraceToolsTests
{
    private const int MaxTraceCalls = 10;

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private McpTxScenario _scenario = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _node = await McpTestNode.Create(c =>
        {
            c.MaxConcurrentToolCalls = 64;
            c.MaxTraceCalls = MaxTraceCalls;
        });
        _scenario = await McpTxScenario.Create(_node);
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [Test]
    public async Task Trace_of_a_forwarding_call_shows_the_nested_value_transfer()
    {
        JsonElement result = await Success(("hash", _scenario.ForwardCall.Hash!.ToString()));

        JsonElement root = result.GetProperty("root");
        JsonElement child = root.GetProperty("calls")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(result.GetProperty("blockNumber").GetUInt64(), Is.EqualTo(_scenario.Block.Number));
            Assert.That(result.GetProperty("totalFrames").GetInt32(), Is.EqualTo(2));
            Assert.That(result.GetProperty("returnedFrames").GetInt32(), Is.EqualTo(2));
            Assert.That(result.GetProperty("truncated").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("CALL"));
            Assert.That(root.GetProperty("from").GetString(), Is.EqualTo(TestItem.AddressB.ToString(true, true)));
            Assert.That(root.GetProperty("to").GetString(), Is.EqualTo(_scenario.Forwarder.ToString(true, true)));
            Assert.That(root.GetProperty("valueFormatted").GetString(), Is.EqualTo("0.000000000000001 ETH"));
            Assert.That(child.GetProperty("type").GetString(), Is.EqualTo("CALL"));
            Assert.That(child.GetProperty("to").GetString(), Is.EqualTo(TestItem.AddressC.ToString(true, true)));
            Assert.That(child.GetProperty("value").GetString(), Is.EqualTo(McpAssert.Hex(McpTxScenario.ForwardValue)));
            Assert.That(child.GetProperty("gasUsed").GetUInt64(), Is.LessThanOrEqualTo(child.GetProperty("gas").GetUInt64()));
        }
    }

    [Test]
    public async Task Trace_of_a_revert_decodes_the_reason()
    {
        JsonElement result = await Success(("hash", _scenario.RevertCall.Hash!.ToString()), ("includeInput", true));

        JsonElement root = result.GetProperty("root");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("failed"));
            Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("execution reverted"));
            Assert.That(root.GetProperty("selector").GetString(), Is.EqualTo("0xa9059cbb"));
            Assert.That(root.GetProperty("method").GetString(), Is.EqualTo("transfer(address,uint256)"));
            Assert.That(root.GetProperty("input").GetString(), Is.EqualTo("0x" + Convert.ToHexStringLower(McpTxScenario.TransferCallData)));
            Assert.That(root.GetProperty("outputSize").GetInt32(), Is.EqualTo(McpTxScenario.RevertData.Length));
            Assert.That(root.GetProperty("revert").GetProperty("message").GetString(), Is.EqualTo(McpTxScenario.RevertReason));
        }
    }

    [Test]
    public async Task Trace_of_an_out_of_gas_call()
    {
        JsonElement result = await Success(("hash", _scenario.OutOfGasCall.Hash!.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("failed"));
            Assert.That(result.GetProperty("root").GetProperty("error").GetString(), Is.EqualTo("out of gas"));
            Assert.That(result.GetProperty("root").TryGetProperty("input", out _), Is.False, "input is only returned with includeInput");
        }
    }

    [Test]
    public async Task Trace_is_truncated_at_the_frame_cap()
    {
        JsonElement result = await Success(("hash", _scenario.RecursiveCall.Hash!.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("returnedFrames").GetInt32(), Is.EqualTo(MaxTraceCalls));
            Assert.That(result.GetProperty("maxFrames").GetInt32(), Is.EqualTo(MaxTraceCalls));
            Assert.That(result.GetProperty("totalFrames").GetInt32(), Is.GreaterThan(MaxTraceCalls));
            Assert.That(CountFrames(result.GetProperty("root")), Is.EqualTo(MaxTraceCalls));
            Assert.That(OmittedCalls(result.GetProperty("root")) + MaxTraceCalls, Is.EqualTo(result.GetProperty("totalFrames").GetInt32()));
        }
    }

    [Test]
    public async Task Trace_stops_at_the_byte_budget_instead_of_failing()
    {
        await using McpTestNode node = await McpTestNode.Create(static c => c.MaxResultSize = 2_000);
        McpTxScenario scenario = await McpTxScenario.Create(node);
        await using McpClient client = await node.CreateClient();

        JsonElement result = McpAssert.Success(await McpToolCalls.Call(client, "trace_transaction",
            [("hash", scenario.RecursiveCall.Hash!.ToString()), ("includeInput", true)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("returnedFrames").GetInt32(), Is.LessThan(result.GetProperty("totalFrames").GetInt32()));
            Assert.That(result.GetProperty("returnedFrames").GetInt32(), Is.GreaterThanOrEqualTo(1));
        }
    }

    [Test]
    public async Task Disabled_tracing_makes_trace_unavailable_and_explain_skip_the_trace()
    {
        await using McpTestNode node = await McpTestNode.Create(static c => c.EnableTracing = false);
        McpTxScenario scenario = await McpTxScenario.Create(node);
        await using McpClient client = await node.CreateClient();
        string hash = scenario.RevertCall.Hash!.ToString();

        JsonElement error = McpAssert.Error(await McpToolCalls.Call(client, "trace_transaction", [("hash", hash)]), McpAssert.Unavailable);
        JsonElement explained = McpAssert.Success(await McpToolCalls.Call(client, "explain_transaction", [("hash", hash)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Mcp.EnableTracing"));
            Assert.That(explained.GetProperty("notes").EnumerateArray().Select(static n => n.GetString()), Has.Some.Contains("tracing is disabled"));
        }
    }

    [Test]
    public async Task Trace_depth_zero_returns_only_the_top_call()
    {
        JsonElement result = await Success(("hash", _scenario.ForwardCall.Hash!.ToString()), ("maxDepth", 0));

        JsonElement root = result.GetProperty("root");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("returnedFrames").GetInt32(), Is.EqualTo(1));
            Assert.That(result.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(root.TryGetProperty("calls", out _), Is.False);
            Assert.That(root.GetProperty("omittedCalls").GetInt32(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Trace_of_a_plain_transfer_has_a_single_frame()
    {
        JsonElement result = await Success(("hash", _scenario.Transfer.Hash!.ToString()));

        Assert.That(result.GetProperty("totalFrames").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task Trace_unknown_hash_is_not_found()
    {
        JsonElement error = McpAssert.Error(await Call(("hash", Keccak.Compute("nope").ToString())), McpAssert.NotFound);
        McpAssert.NoInternals(error);
    }

    [TestCase("0x12")]
    [TestCase("not-a-hash")]
    public async Task Trace_invalid_hash(string hash) =>
        McpAssert.Error(await Call(("hash", hash)), McpAssert.InvalidInput);

    [TestCase(-1)]
    [TestCase(25)]
    public async Task Trace_invalid_depth(int depth) =>
        McpAssert.Error(await Call(("hash", _scenario.Transfer.Hash!.ToString()), ("maxDepth", depth)), McpAssert.InvalidInput);

    [Test]
    public async Task Trace_tool_is_listed_read_only_with_output_schema()
    {
        McpClientTool tool = (await _client.ListToolsAsync()).Single(t => t.Name == "trace_transaction");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null);
            Assert.That(tool.ProtocolTool.Annotations?.ReadOnlyHint, Is.True);
            Assert.That(tool.Description, Does.Contain($"at most {MaxTraceCalls} frames"));
        }
    }

    private static int CountFrames(JsonElement frame)
    {
        int count = 1;
        if (frame.TryGetProperty("calls", out JsonElement calls))
        {
            foreach (JsonElement child in calls.EnumerateArray()) count += CountFrames(child);
        }

        return count;
    }

    private static int OmittedCalls(JsonElement frame)
    {
        int omitted = frame.TryGetProperty("omittedCalls", out JsonElement value) ? value.GetInt32() : 0;
        if (frame.TryGetProperty("calls", out JsonElement calls))
        {
            foreach (JsonElement child in calls.EnumerateArray()) omitted += OmittedCalls(child);
        }

        return omitted;
    }

    private async Task<JsonElement> Success(params (string Name, object? Value)[] args)
    {
        CallToolResult result = await Call(args);
        JsonElement value = McpAssert.Success(result);
        await McpToolCalls.AssertConformsToOutputSchema(_client, "trace_transaction", result);
        return value;
    }

    private Task<CallToolResult> Call(params (string Name, object? Value)[] args) => McpToolCalls.Call(_client, "trace_transaction", args);
}
