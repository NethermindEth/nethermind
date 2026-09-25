// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// End-to-end tool tests: the official MCP SDK client talks Streamable HTTP to a real <see cref="McpHost"/> serving a
/// production-wired test chain, so results come from the production eth module and JSON-RPC serializer.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpToolsTests
{
    private const string IdentityPrecompile = "0x0000000000000000000000000000000000000004";
    private static readonly string UnknownHash = Keccak.Compute("unknown").ToString();

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private SeededChain _seeded = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _node = await McpTestNode.Create(c => c.MaxConcurrentToolCalls = 64);
        _seeded = await _node.Seed();
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [Test]
    public async Task Lists_exactly_the_read_only_tools()
    {
        IList<McpClientTool> tools = await _client.ListToolsAsync();

        Assert.That(tools.Select(static t => t.Name), Is.EquivalentTo(McpAssert.ToolNames));
        foreach (McpClientTool tool in tools)
        {
            ToolAnnotations? annotations = tool.ProtocolTool.Annotations;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(annotations, Is.Not.Null, tool.Name);
                Assert.That(annotations?.ReadOnlyHint, Is.True, $"{tool.Name} readOnlyHint");
                Assert.That(annotations?.DestructiveHint, Is.False, $"{tool.Name} destructiveHint");
                Assert.That(annotations?.IdempotentHint, Is.True, $"{tool.Name} idempotentHint");
                Assert.That(annotations?.OpenWorldHint, Is.False, $"{tool.Name} openWorldHint");
                Assert.That(tool.Description, Is.Not.Null.And.Not.Empty, $"{tool.Name} description");
                Assert.That(tool.ProtocolTool.InputSchema.GetProperty("type").GetString(), Is.EqualTo("object"), $"{tool.Name} input schema");
            }
        }
    }

    [TestCase("chain_info", new string[0])]
    [TestCase("get_block", new[] { "block" })]
    [TestCase("get_transaction", new[] { "hash" })]
    [TestCase("get_transaction_receipt", new[] { "hash" })]
    [TestCase("get_balance", new[] { "address" })]
    [TestCase("get_code", new[] { "address" })]
    [TestCase("get_logs", new[] { "fromBlock", "toBlock" })]
    [TestCase("call", new[] { "to", "data", "gas" })]
    public async Task Tool_schema_requires_exactly_the_mandatory_arguments(string toolName, string[] required)
    {
        McpClientTool tool = (await _client.ListToolsAsync()).Single(t => t.Name == toolName);

        string[] actual = tool.ProtocolTool.InputSchema.TryGetProperty("required", out JsonElement list)
            ? list.EnumerateArray().Select(static e => e.GetString()!).ToArray()
            : [];
        Assert.That(actual, Is.EquivalentTo(required));
    }

    [Test]
    public async Task Chain_info_returns_chain_id_and_head()
    {
        JsonElement result = McpAssert.Success(await Call("chain_info"));

        Block head = _node.Chain.BlockTree.Head!;
        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(result.GetProperty("chainId"), McpAssert.Hex(_node.Chain.SpecProvider.ChainId));
            McpAssert.Quantity(result.GetProperty("headNumber"), McpAssert.Hex(head.Number));
            Assert.That(result.GetProperty("headHash").GetString(), Is.EqualTo(head.Hash!.ToString()));
            Assert.That(result.GetProperty("chainIdDecimal").GetUInt64(), Is.EqualTo(_node.Chain.SpecProvider.ChainId));
            Assert.That(result.GetProperty("headNumberDecimal").GetUInt64(), Is.EqualTo(head.Number));
            Assert.That(result.GetProperty("networkName").GetString(), Is.Not.Empty);
            Assert.That(result.GetProperty("nativeCurrency").GetString(), Is.EqualTo("ETH"));
            Assert.That(result.GetProperty("isGnosisFamily").GetBoolean(), Is.False);
            McpAssert.Quantity(result.GetProperty("headTimestamp"), McpAssert.Hex(head.Timestamp));
            Assert.That(result.GetProperty("headTimestampIso").GetString(),
                Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds((long)head.Timestamp).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
            Assert.That(result.GetProperty("wellKnownContracts").ValueKind, Is.EqualTo(JsonValueKind.Array));
        }
    }

    [TestCase("latest")]
    [TestCase("{number:hex}")]
    [TestCase("{number:dec}")]
    [TestCase("{hash}")]
    [TestCase("{HASH}")]
    public async Task Get_block_by_selector_returns_the_seeded_block(string selector)
    {
        JsonElement block = McpAssert.Success(await Call("get_block", ("block", Selector(selector))));

        AssertSeededBlock(block);
        using (Assert.EnterMultipleScope())
        {
            foreach (JsonElement tx in block.GetProperty("transactions").EnumerateArray())
            {
                Assert.That(tx.ValueKind, Is.EqualTo(JsonValueKind.String), "transactions are hashes by default");
                McpAssert.Data(tx, Hash256.Size);
            }
        }
    }

    [Test]
    public async Task Get_block_with_full_transactions_returns_transaction_objects()
    {
        JsonElement block = McpAssert.Success(await Call("get_block", ("block", "latest"), ("fullTransactions", true)));

        AssertSeededBlock(block);
        JsonElement first = block.GetProperty("transactions")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(first.GetProperty("hash").GetString(), Is.EqualTo(_seeded.Transfer.Hash!.ToString()));
            Assert.That(first.GetProperty("from").GetString(), Is.EqualTo(TestItem.AddressB.ToString()));
        }
    }

    [TestCase("earliest", 0)]
    [TestCase("0x0", 0)]
    [TestCase("0", 0)]
    [TestCase("0x00", 0)]
    public async Task Get_block_genesis(string selector, int expectedNumber)
    {
        JsonElement block = McpAssert.Success(await Call("get_block", ("block", selector)));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(block.GetProperty("number"), McpAssert.Hex((ulong)expectedNumber));
            Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(_node.Chain.BlockTree.Genesis!.Hash!.ToString()));
        }
    }

    [Test]
    public async Task Get_transaction_returns_the_transfer()
    {
        JsonElement tx = McpAssert.Success(await Call("get_transaction", ("hash", _seeded.Transfer.Hash!.ToString())));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.GetProperty("hash").GetString(), Is.EqualTo(_seeded.Transfer.Hash!.ToString()));
            Assert.That(tx.GetProperty("from").GetString(), Is.EqualTo(TestItem.AddressB.ToString()));
            Assert.That(tx.GetProperty("to").GetString(), Is.EqualTo(TestItem.AddressC.ToString()));
            McpAssert.Quantity(tx.GetProperty("value"), McpAssert.Hex(SeededChain.TransferValue));
            McpAssert.Quantity(tx.GetProperty("blockNumber"), McpAssert.Hex(_seeded.Block.Number));
            Assert.That(tx.GetProperty("blockHash").GetString(), Is.EqualTo(_seeded.Block.Hash!.ToString()));
            McpAssert.Quantity(tx.GetProperty("nonce"));
            McpAssert.Quantity(tx.GetProperty("gas"), McpAssert.Hex(GasCostOf.Transaction));
            McpAssert.Quantity(tx.GetProperty("transactionIndex"), "0x0");
        }
    }

    [Test]
    public async Task Get_transaction_receipt_returns_status_contract_and_logs()
    {
        JsonElement receipt = McpAssert.Success(await Call("get_transaction_receipt", ("hash", _seeded.LogDeploy.Hash!.ToString())));

        JsonElement logs = receipt.GetProperty("logs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GetProperty("transactionHash").GetString(), Is.EqualTo(_seeded.LogDeploy.Hash!.ToString()));
            McpAssert.Quantity(receipt.GetProperty("status"), "0x1");
            McpAssert.Quantity(receipt.GetProperty("gasUsed"));
            McpAssert.Quantity(receipt.GetProperty("blockNumber"), McpAssert.Hex(_seeded.Block.Number));
            Assert.That(receipt.GetProperty("contractAddress").GetString(), Is.EqualTo(_seeded.LogContract.ToString()));
            Assert.That(logs.GetArrayLength(), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Get_transaction_receipt_of_transfer_has_no_contract()
    {
        JsonElement receipt = McpAssert.Success(await Call("get_transaction_receipt", ("hash", _seeded.Transfer.Hash!.ToString())));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(receipt.GetProperty("status"), "0x1");
            McpAssert.Quantity(receipt.GetProperty("gasUsed"), McpAssert.Hex(GasCostOf.Transaction));
            Assert.That(!receipt.TryGetProperty("contractAddress", out JsonElement contract) || contract.ValueKind == JsonValueKind.Null, Is.True);
            Assert.That(receipt.GetProperty("logs").GetArrayLength(), Is.Zero);
        }
    }

    [TestCase("latest", true)]
    [TestCase("{number:hex}", true)]
    [TestCase("{hash}", true)]
    [TestCase("earliest", false)]
    public async Task Get_balance_at_block(string selector, bool afterTransfer)
    {
        JsonElement balance = McpAssert.Success(await Call("get_balance", ("address", TestItem.AddressC.ToString()), ("block", Selector(selector))));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(balance.GetProperty("balance"), McpAssert.Hex(afterTransfer ? TestBlockchain.InitialValue + SeededChain.TransferValue : TestBlockchain.InitialValue));
            Assert.That(balance.GetProperty("balanceFormatted").GetString(), Is.EqualTo(afterTransfer ? "1000.000000000000001234" : "1000"));
            Assert.That(balance.GetProperty("symbol").GetString(), Is.EqualTo("ETH"));
            Assert.That(balance.GetProperty("decimals").GetInt32(), Is.EqualTo(18));
        }
    }

    [Test]
    public async Task Get_balance_defaults_to_latest_and_encodes_zero_as_0x0()
    {
        JsonElement latest = McpAssert.Success(await Call("get_balance", ("address", TestItem.AddressC.ToString())));
        JsonElement empty = McpAssert.Success(await Call("get_balance", ("address", TestItem.GetRandomAddress().ToString())));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(latest.GetProperty("balance"), McpAssert.Hex(TestBlockchain.InitialValue + SeededChain.TransferValue));
            McpAssert.Quantity(empty.GetProperty("balance"), "0x0");
            Assert.That(empty.GetProperty("balanceFormatted").GetString(), Is.EqualTo("0"));
        }
    }

    private static IEnumerable<TestCaseData> CodeCases()
    {
        yield return new TestCaseData("return", Bytes.ToHexString(SeededChain.ReturnRuntimeCode, true)).SetName("Get_code_of_deployed_contract");
        yield return new TestCaseData("genesis", "0xabcd").SetName("Get_code_of_genesis_account");
        yield return new TestCaseData("eoa", "0x").SetName("Get_code_of_eoa_is_empty");
    }

    [TestCaseSource(nameof(CodeCases))]
    public async Task Get_code(string account, string expected)
    {
        Address address = account switch
        {
            "return" => _seeded.ReturnContract,
            "genesis" => TestItem.AddressA,
            _ => TestItem.AddressC
        };

        JsonElement code = McpAssert.Success(await Call("get_code", ("address", address.ToString())));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Data(code);
            Assert.That(code.GetString(), Is.EqualTo(expected));
        }
    }

    private static IEnumerable<TestCaseData> LogFilterCases()
    {
        string topicA = SeededChain.TopicA.ToString();
        string topicB = SeededChain.TopicB.ToString();
        yield return new TestCaseData(null, null, 2).SetName("Get_logs_without_filter");
        yield return new TestCaseData(new[] { "log" }, null, 2).SetName("Get_logs_by_address");
        yield return new TestCaseData(new[] { "return" }, null, 0).SetName("Get_logs_by_other_address");
        yield return new TestCaseData(new[] { "return", "log" }, null, 2).SetName("Get_logs_by_address_list");
        yield return new TestCaseData(null, new object?[] { topicA }, 2).SetName("Get_logs_by_topic0");
        yield return new TestCaseData(null, new object?[] { null, topicB }, 1).SetName("Get_logs_by_topic1_with_wildcard");
        yield return new TestCaseData(null, new object?[] { new[] { topicB, topicA } }, 2).SetName("Get_logs_by_topic_alternatives");
        yield return new TestCaseData(null, new object?[] { topicB }, 0).SetName("Get_logs_by_unmatched_topic0");
        yield return new TestCaseData(new[] { "log" }, new object?[] { topicA, topicB }, 1).SetName("Get_logs_by_address_and_topics");
    }

    [TestCaseSource(nameof(LogFilterCases))]
    public async Task Get_logs(string[]? addresses, object?[]? topics, int expectedCount)
    {
        List<(string, object?)> args = [("fromBlock", "earliest"), ("toBlock", "latest")];
        if (addresses is not null) args.Add(("address", Json(addresses.Select(a => (a == "log" ? _seeded.LogContract : _seeded.ReturnContract).ToString()).ToArray())));
        if (topics is not null) args.Add(("topics", Json(topics)));

        JsonElement page = McpAssert.Success(await Call("get_logs", [.. args]));
        JsonElement logs = page.GetProperty("logs");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs.GetArrayLength(), Is.EqualTo(expectedCount));
            Assert.That(page.GetProperty("truncated").GetBoolean(), Is.False);
            Assert.That(page.TryGetProperty("nextCursor", out _), Is.False);
            McpAssert.Quantity(page.GetProperty("fromBlock"), "0x0");
            McpAssert.Quantity(page.GetProperty("toBlock"), McpAssert.Hex(_node.Chain.BlockTree.Head!.Number));
        }
        foreach (JsonElement log in logs.EnumerateArray())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(log.GetProperty("address").GetString(), Is.EqualTo(_seeded.LogContract.ToString()));
                Assert.That(log.GetProperty("topics")[0].GetString(), Is.EqualTo(SeededChain.TopicA.ToString()));
                Assert.That(log.GetProperty("data").GetString(), Is.EqualTo(Bytes.ToHexString(SeededChain.LogData, true)));
                Assert.That(log.GetProperty("transactionHash").GetString(), Is.EqualTo(_seeded.LogDeploy.Hash!.ToString()));
                McpAssert.Quantity(log.GetProperty("blockNumber"), McpAssert.Hex(_seeded.Block.Number));
                McpAssert.Quantity(log.GetProperty("logIndex"));
                Assert.That(log.GetProperty("removed").GetBoolean(), Is.False);
            }
        }
    }

    [TestCase("0x0102030405", "0x0102030405")]
    [TestCase("0x", "0x")]
    public async Task Call_identity_precompile_echoes_input(string data, string expected)
    {
        JsonElement output = McpAssert.Success(await Call("call", ("to", IdentityPrecompile), ("data", data), ("gas", "100000")));

        Assert.That(output.GetString(), Is.EqualTo(expected));
    }

    [TestCase("latest")]
    [TestCase("{number:hex}")]
    public async Task Call_deployed_contract_returns_its_word(string selector)
    {
        JsonElement output = McpAssert.Success(await Call("call",
            ("to", _seeded.ReturnContract.ToString()), ("data", "0x12345678"), ("gas", "0x186a0"),
            ("from", TestItem.AddressB.ToString()), ("block", Selector(selector))));

        Assert.That(output.GetString(), Is.EqualTo(Bytes.ToHexString(SeededChain.ReturnWord, true)));
    }

    [Test]
    public async Task Call_before_deployment_returns_empty_output()
    {
        JsonElement output = McpAssert.Success(await Call("call",
            ("to", _seeded.ReturnContract.ToString()), ("data", "0x"), ("gas", "100000"), ("block", "earliest")));

        Assert.That(output.GetString(), Is.EqualTo("0x"));
    }

    [Test]
    public async Task Call_revert_returns_execution_reverted_with_data()
    {
        JsonElement error = McpAssert.Error(
            await Call("call", ("to", _seeded.RevertContract.ToString()), ("data", "0x"), ("gas", "100000")),
            McpAssert.ExecutionReverted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("data").GetString(), Is.EqualTo(Bytes.ToHexString(SeededChain.RevertData, true)));
            Assert.That(error.GetProperty("reason").GetProperty("kind").GetString(), Is.Not.Empty);
            McpAssert.NoInternals(error);
        }
    }

    private static IEnumerable<TestCaseData> NotFoundCases()
    {
        yield return new TestCaseData("get_block", new[] { "block", UnknownHash }).SetName("Get_block_unknown_hash_is_not_found");
        yield return new TestCaseData("get_block", new[] { "block", "0x100" }).SetName("Get_block_beyond_head_is_not_found");
        yield return new TestCaseData("get_block", new[] { "block", "18446744073709551615" }).SetName("Get_block_max_number_is_not_found");
        yield return new TestCaseData("get_transaction", new[] { "hash", UnknownHash }).SetName("Get_transaction_unknown_is_not_found");
        yield return new TestCaseData("get_transaction_receipt", new[] { "hash", UnknownHash }).SetName("Get_receipt_unknown_is_not_found");
        yield return new TestCaseData("get_logs", new[] { "fromBlock", UnknownHash, "toBlock", "latest" }).SetName("Get_logs_unknown_block_hash_is_not_found");
    }

    [TestCaseSource(nameof(NotFoundCases))]
    public async Task Missing_data_is_not_found(string toolName, string[] args)
    {
        JsonElement error = McpAssert.Error(await Call(toolName, Pairs(args)), McpAssert.NotFound);
        McpAssert.NoInternals(error);
    }

    [TestCase("0x100")]
    [TestCase(null)] // the unknown hash
    public async Task State_at_unknown_block_is_not_served(string? block)
    {
        JsonElement error = McpAssert.Error(
            await Call("get_balance", ("address", TestItem.AddressC.ToString()), ("block", block ?? UnknownHash)),
            McpAssert.NotFound, McpAssert.Unavailable, McpAssert.InvalidInput);
        McpAssert.NoInternals(error);
    }

    private static IEnumerable<TestCaseData> InvalidInputCases()
    {
        string address = TestItem.AddressC.ToString();
        string hash = Keccak.Compute("x").ToString();

        foreach (string badAddress in new[] { "", "0x", "0x1234", "C" + address[1..], address[2..], address + "00", address[..^1] + "g", "not-an-address" })
            yield return Case("get_balance", $"bad address '{badAddress}'", "address", badAddress);

        foreach (string badBlock in new[] { "pending", "PENDING", "-1", "0x", "0x-1", "1.5", "0x1ffffffffffffffff", "18446744073709551616", "latest ", " latest", "1e3" })
            yield return Case("get_balance", $"bad block '{badBlock}'", "address", address, "block", badBlock);

        foreach (string badHash in new[] { "", "0x", hash[..^1], hash + "0", hash[2..], "0x" + new string('z', 64) })
        {
            yield return Case("get_transaction", $"bad hash '{badHash}'", "hash", badHash);
            yield return Case("get_transaction_receipt", $"bad hash '{badHash}'", "hash", badHash);
        }

        yield return Case("get_code", "bad block", "address", address, "block", "pending");
        yield return Case("get_block", "pending block", "block", "pending");
        yield return Case("get_block", "negative block", "block", "-5");

        yield return Case("call", "odd hex data", "to", IdentityPrecompile, "data", "0x123", "gas", "100000");
        yield return Case("call", "unprefixed data", "to", IdentityPrecompile, "data", "1234", "gas", "100000");
        yield return Case("call", "non-hex data", "to", IdentityPrecompile, "data", "0xzz", "gas", "100000");
        yield return Case("call", "bad target", "to", "0x04", "data", "0x", "gas", "100000");
        yield return Case("call", "bad sender", "to", IdentityPrecompile, "data", "0x", "gas", "100000", "from", "0x01");
        yield return Case("call", "zero gas", "to", IdentityPrecompile, "data", "0x", "gas", "0");
        yield return Case("call", "negative gas", "to", IdentityPrecompile, "data", "0x", "gas", "-1");
        yield return Case("call", "non-numeric gas", "to", IdentityPrecompile, "data", "0x", "gas", "lots");
        yield return Case("call", "overflowing gas", "to", IdentityPrecompile, "data", "0x", "gas", "0x10000000000000000");
        yield return Case("call", "gas above cap", "to", IdentityPrecompile, "data", "0x", "gas", "50000001");
        yield return Case("call", "negative value", "to", IdentityPrecompile, "data", "0x", "gas", "100000", "value", "-1");
        yield return Case("call", "overflowing value", "to", IdentityPrecompile, "data", "0x", "gas", "100000", "value", "0x1" + new string('0', 64));
        yield return Case("call", "pending block", "to", IdentityPrecompile, "data", "0x", "gas", "100000", "block", "pending");

        yield return Case("get_logs", "reversed range", "fromBlock", "0x1", "toBlock", "0x0");
        yield return Case("get_logs", "pending toBlock", "fromBlock", "0x0", "toBlock", "pending");
    }

    [TestCaseSource(nameof(InvalidInputCases))]
    public async Task Invalid_input_is_rejected(string toolName, string[] args)
    {
        JsonElement error = McpAssert.Error(await Call(toolName, Pairs(args)), McpAssert.InvalidInput);
        McpAssert.NoInternals(error);
    }

    private static IEnumerable<TestCaseData> InvalidLogFilterCases()
    {
        string topic = SeededChain.TopicA.ToString();
        yield return new TestCaseData(null, new object?[] { topic, topic, topic, topic, topic }).SetName("Get_logs_five_topic_positions_is_invalid");
        yield return new TestCaseData(null, new object?[] { "0x1234" }).SetName("Get_logs_short_topic_is_invalid");
        yield return new TestCaseData(null, new object?[] { 42 }).SetName("Get_logs_numeric_topic_is_invalid");
        yield return new TestCaseData(null, new object?[] { new object?[] { topic, "nope" } }).SetName("Get_logs_bad_topic_alternative_is_invalid");
        yield return new TestCaseData(null, new object?[] { Enumerable.Repeat(topic, 33).ToArray() }).SetName("Get_logs_too_many_topic_alternatives_is_invalid");
        yield return new TestCaseData(new[] { "0x01" }, null).SetName("Get_logs_bad_address_is_invalid");
        yield return new TestCaseData(Enumerable.Range(0, 33).Select(static _ => TestItem.GetRandomAddress().ToString()).ToArray(), null).SetName("Get_logs_too_many_addresses_is_invalid");
    }

    [TestCaseSource(nameof(InvalidLogFilterCases))]
    public async Task Invalid_log_filter_is_rejected(string[]? addresses, object?[]? topics)
    {
        List<(string, object?)> args = [("fromBlock", "earliest"), ("toBlock", "latest")];
        if (addresses is not null) args.Add(("address", Json(addresses)));
        if (topics is not null) args.Add(("topics", Json(topics)));

        JsonElement error = McpAssert.Error(await Call("get_logs", [.. args]), McpAssert.InvalidInput);
        McpAssert.NoInternals(error);
    }

    [TestCase("get_balance")]
    [TestCase("get_transaction")]
    [TestCase("call")]
    public async Task Missing_required_argument_is_rejected(string toolName)
    {
        CallToolResult? result = null;
        McpProtocolException? protocolError = null;
        try
        {
            result = await Call(toolName);
        }
        catch (McpProtocolException e)
        {
            protocolError = e;
        }

        Assert.That(protocolError is not null || result?.IsError == true, Is.True,
            "a call without its required arguments must fail, either as a protocol error or as a tool error");
    }

    private static IEnumerable<TestCaseData> SchemaCases()
    {
        yield return new TestCaseData("chain_info", Array.Empty<string>()).SetName("Output_schema_chain_info");
        yield return new TestCaseData("get_block", new[] { "block", "latest" }).SetName("Output_schema_get_block");
        yield return new TestCaseData("get_block", new[] { "block", "latest", "fullTransactions", "true" }).SetName("Output_schema_get_block_full");
        yield return new TestCaseData("get_transaction", new[] { "hash", "{transfer}" }).SetName("Output_schema_get_transaction");
        yield return new TestCaseData("get_transaction_receipt", new[] { "hash", "{logDeploy}" }).SetName("Output_schema_get_transaction_receipt");
        yield return new TestCaseData("get_transaction_receipt", new[] { "hash", "{transfer}" }).SetName("Output_schema_get_transaction_receipt_transfer");
        yield return new TestCaseData("get_balance", new[] { "address", "{addressC}" }).SetName("Output_schema_get_balance");
        yield return new TestCaseData("get_code", new[] { "address", "{return}" }).SetName("Output_schema_get_code");
        yield return new TestCaseData("get_logs", new[] { "fromBlock", "earliest", "toBlock", "latest" }).SetName("Output_schema_get_logs");
        yield return new TestCaseData("get_logs", new[] { "fromBlock", "earliest", "toBlock", "latest", "limit", "1" }).SetName("Output_schema_get_logs_truncated");
        yield return new TestCaseData("call", new[] { "to", IdentityPrecompile, "data", "0x01", "gas", "100000" }).SetName("Output_schema_call");
    }

    [TestCaseSource(nameof(SchemaCases))]
    public async Task Output_conforms_to_the_declared_schema(string toolName, string[] args)
    {
        (string Name, object? Value)[] pairs = Pairs(args);
        for (int i = 0; i < pairs.Length; i++)
        {
            pairs[i].Value = pairs[i].Value switch
            {
                "{transfer}" => _seeded.Transfer.Hash!.ToString(),
                "{logDeploy}" => _seeded.LogDeploy.Hash!.ToString(),
                "{addressC}" => TestItem.AddressC.ToString(),
                "{return}" => _seeded.ReturnContract.ToString(),
                "true" => true,
                "1" => 1,
                string other => other,
                _ => null
            };
        }

        McpClientTool tool = (await _client.ListToolsAsync()).Single(t => t.Name == toolName);
        CallToolResult result = await Call(toolName, pairs);
        McpAssert.Success(result);

        Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null, $"{toolName} must declare an output schema");
        McpSchemaValidator.AssertConforms(tool.ProtocolTool.OutputSchema!.Value, result.StructuredContent!.Value);
    }

    private void AssertSeededBlock(JsonElement block)
    {
        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(block.GetProperty("number"), McpAssert.Hex(_seeded.Block.Number));
            Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(_seeded.Block.Hash!.ToString()));
            Assert.That(block.GetProperty("parentHash").GetString(), Is.EqualTo(_seeded.Block.ParentHash!.ToString()));
            McpAssert.Quantity(block.GetProperty("gasUsed"), McpAssert.Hex(_seeded.Block.GasUsed));
            McpAssert.Quantity(block.GetProperty("gasLimit"), McpAssert.Hex(_seeded.Block.GasLimit));
            McpAssert.Quantity(block.GetProperty("timestamp"));
            McpAssert.Data(block.GetProperty("stateRoot"), Hash256.Size);
            McpAssert.Data(block.GetProperty("logsBloom"), 256);
            Assert.That(block.GetProperty("transactions").GetArrayLength(), Is.EqualTo(_seeded.Block.Transactions.Length));
        }
    }

    private string Selector(string selector) => selector switch
    {
        "{number:hex}" => McpAssert.Hex(_seeded.Block.Number),
        "{number:dec}" => _seeded.Block.Number.ToString(),
        "{hash}" => _seeded.Block.Hash!.ToString(),
        "{HASH}" => "0x" + _seeded.Block.Hash!.ToString()[2..].ToUpperInvariant(),
        _ => selector
    };

    private Task<CallToolResult> Call(string toolName, params (string Name, object? Value)[] args) =>
        McpToolCalls.Call(_client, toolName, args);

    private static TestCaseData Case(string toolName, string description, params string[] args) =>
        new TestCaseData(toolName, args).SetName($"Invalid_input_{toolName}_{description}");

    private static (string, object?)[] Pairs(string[] args)
    {
        (string, object?)[] pairs = new (string, object?)[args.Length / 2];
        for (int i = 0; i < pairs.Length; i++) pairs[i] = (args[2 * i], args[2 * i + 1]);
        return pairs;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>Invokes MCP tools through the SDK client.</summary>
internal static class McpToolCalls
{
    public static async Task<CallToolResult> Call(McpClient client, string toolName, (string Name, object? Value)[] args, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> arguments = new(args.Length);
        foreach ((string name, object? value) in args) arguments[name] = value;
        return await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
    }
}
