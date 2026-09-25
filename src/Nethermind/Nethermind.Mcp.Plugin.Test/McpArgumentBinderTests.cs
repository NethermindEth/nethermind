// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Core.Test.Builders;
using Nethermind.Mcp.Plugin.Tools;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>Lenient argument binding: coercions of common LLM argument mistakes, and invalid_input instead of SDK binding failures.</summary>
[Parallelizable(ParallelScope.Self)]
public class McpArgumentBinderTests
{
    private static readonly McpArgumentBinder.Parameter[] Parameters = McpArgumentBinder.Describe(
        typeof(McpArgumentBinderTests).GetMethod(nameof(Sample), BindingFlags.NonPublic | BindingFlags.Static)!);

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private SeededChain _seeded = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _node = await McpTestNode.Create(static c => c.MaxConcurrentToolCalls = 64);
        _seeded = await _node.Seed();
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [TestCase("""{"text": 19553778}""", "text", "\"19553778\"", TestName = "Number_becomes_string")]
    [TestCase("""{"text": true}""", "text", "\"true\"", TestName = "Boolean_becomes_string")]
    [TestCase("""{"text": "a", "list": "0xabc"}""", "list", """["0xabc"]""", TestName = "Single_string_is_wrapped_into_a_string_array")]
    [TestCase("""{"text": "a", "list": [1, "b"]}""", "list", """["1","b"]""", TestName = "Numbers_inside_a_string_array_become_strings")]
    [TestCase("""{"text": "a", "list": "[\"x\",\"y\"]"}""", "list", """["x","y"]""", TestName = "String_holding_a_json_array_is_parsed_for_a_string_array")]
    [TestCase("""{"text": "a", "items": "[1, {\"a\": 2}]"}""", "items", """[1,{"a":2}]""", TestName = "String_holding_a_json_array_is_parsed")]
    [TestCase("""{"text": "a", "items": "0xabc"}""", "items", """["0xabc"]""", TestName = "Plain_string_is_wrapped_into_a_json_array")]
    [TestCase("""{"text": "a", "items": {"to": "0x1"}}""", "items", """[{"to":"0x1"}]""", TestName = "Single_object_is_wrapped_into_a_json_array")]
    [TestCase("""{"text": "a", "items": "{\"to\": \"0x1\"}"}""", "items", """[{"to":"0x1"}]""", TestName = "String_holding_an_object_is_wrapped_into_a_json_array")]
    [TestCase("""{"text": "a", "json": "{\"0x1\": {\"balance\": \"0x1\"}}"}""", "json", """{"0x1":{"balance":"0x1"}}""", TestName = "String_holding_an_object_is_parsed")]
    [TestCase("""{"text": "a", "flag": "true"}""", "flag", "true", TestName = "String_boolean_is_parsed")]
    [TestCase("""{"text": "a", "numbers": 50}""", "numbers", "[50]", TestName = "Single_number_is_wrapped_into_a_number_array")]
    [TestCase("""{"text": "a", "count": "12"}""", "count", "\"12\"", TestName = "Numeric_string_is_left_for_the_sdk_to_read")]
    [TestCase("""{"alias": "a"}""", "text", "\"a\"", TestName = "Alias_is_accepted")]
    public void Arguments_are_coerced(string arguments, string name, string expected)
    {
        Dictionary<string, JsonElement>? result = McpArgumentBinder.Normalize(Parameters, Parse(arguments), out string? error);

        Assert.That(result, Is.Not.Null, error);
        Assert.That(result![name].GetRawText().Replace(" ", string.Empty), Is.EqualTo(expected.Replace(" ", string.Empty)));
    }

    [Test]
    public void Null_for_an_optional_parameter_is_treated_as_omitted()
    {
        Dictionary<string, JsonElement>? result = McpArgumentBinder.Normalize(Parameters, Parse("""{"text": "a", "list": null, "count": null}"""), out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Not.Null, error);
            Assert.That(result!.ContainsKey("list"), Is.False);
            Assert.That(result.ContainsKey("count"), Is.False);
        }
    }

    [TestCase("""{}""", "'text' is required", TestName = "Missing_required_parameter_is_named")]
    [TestCase("""{"text": null}""", "'text' is required", TestName = "Null_required_parameter_is_named")]
    [TestCase("""{"text": {"a": 1}}""", "'text' must be a string; got an object", TestName = "Object_for_a_string_is_rejected")]
    [TestCase("""{"text": "a", "count": "twelve"}""", "'count' must be an integer", TestName = "Non_numeric_string_for_an_integer_is_rejected")]
    [TestCase("""{"text": "a", "count": 1e30}""", "'count' must be an integer", TestName = "Integer_overflow_is_rejected")]
    [TestCase("""{"text": "a", "flag": "maybe"}""", "'flag' must be true or false", TestName = "Non_boolean_string_is_rejected")]
    public void Unbindable_arguments_name_the_parameter_and_type(string arguments, string expected)
    {
        Dictionary<string, JsonElement>? result = McpArgumentBinder.Normalize(Parameters, Parse(arguments), out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(error, Does.StartWith(expected));
        }
    }

    [Test]
    public async Task Number_block_selector_is_accepted_end_to_end()
    {
        JsonElement block = McpAssert.Success(await Call("get_block", ("block", (long)_seeded.Block.Number)));

        McpAssert.Quantity(block.GetProperty("number"), McpAssert.Hex(_seeded.Block.Number));
    }

    [Test]
    public async Task Explicit_null_uses_the_default_end_to_end()
    {
        JsonElement balance = McpAssert.Success(await Call("get_balance", ("address", TestItem.AddressC.ToString()), ("block", null)));

        McpAssert.Quantity(balance.GetProperty("balance"));
    }

    [Test]
    public async Task Single_address_is_accepted_for_the_log_address_list()
    {
        string block = McpAssert.Hex(_seeded.Block.Number);

        JsonElement page = McpAssert.Success(await Call("get_logs", ("fromBlock", block), ("toBlock", block), ("address", _seeded.LogContract.ToString())));

        Assert.That(page.GetProperty("logs").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task Json_string_arguments_are_parsed_for_call_function()
    {
        JsonElement result = McpAssert.Success(await Call("call_function", ("to", _seeded.ReturnContract.ToString()),
            ("signature", "value(uint256)(uint256)"), ("args", "[\"7\"]")));

        Assert.That(result.GetProperty("outputs")[0].GetProperty("value").GetString(), Is.EqualTo("3405691582"));
    }

    [Test]
    public async Task Hash_is_an_alias_of_txHash_in_decode_logs()
    {
        JsonElement result = McpAssert.Success(await Call("decode_logs", ("hash", _seeded.LogDeploy.Hash!.ToString())));

        Assert.That(result.GetProperty("total").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public async Task Unbindable_argument_is_invalid_input_not_a_generic_error()
    {
        JsonElement error = McpAssert.Error(await Call("get_block", ("block", new Dictionary<string, object> { ["number"] = 1 })), McpAssert.InvalidInput);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("'block' must be a string"));
            McpAssert.NoInternals(error);
        }
    }

    private Task<CallToolResult> Call(string tool, params (string Name, object? Value)[] arguments) => McpToolCalls.Call(_client, tool, arguments);

    private static Dictionary<string, JsonElement> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // The parameter shapes the tools use; only the signature matters.
    private static void Sample(
        [McpParameterAlias("alias")] string text,
        string[]? list = null,
        JsonElement[]? items = null,
        JsonElement? json = null,
        double[]? numbers = null,
        bool flag = false,
        int? count = null,
        CancellationToken cancellationToken = default) =>
        _ = (text, list, items, json, numbers, flag, count, cancellationToken);
}
