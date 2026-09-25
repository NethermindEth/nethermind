// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// End-to-end tests of <c>get_storage_at</c>, <c>estimate_gas</c>, <c>fee_estimate</c>, <c>get_proof</c> and
/// <c>get_block_receipts</c> on production-wired test chains.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpEthToolsExtraTests
{
    private const string BigSlot = "0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563";
    private static readonly string UnknownHash = Keccak.Compute("unknown").ToString();

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private SeededChain _seeded = null!;
    private Address _storageContract = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _node = await McpTestNode.Create(c => c.MaxConcurrentToolCalls = 64);
        _seeded = await _node.Seed();
        _storageContract = await DeployStorageContract(_node);
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [TestCase("0x0", "0x000000000000000000000000000000000000000000000000000000000000002a", "42", false)]
    [TestCase("0", "0x000000000000000000000000000000000000000000000000000000000000002a", "42", false)]
    [TestCase("1", null, null, true)]
    [TestCase(BigSlot, "0x0000000000000000000000000000000000000000000000000000000000000001", "1", false)]
    [TestCase("0x7", "0x0000000000000000000000000000000000000000000000000000000000000000", "0", false)]
    public async Task Get_storage_at_reads_the_slot(string slot, string? expectedValue, string? expectedUint, bool hasAddress)
    {
        JsonElement result = McpAssert.Success(await Call("get_storage_at", ("address", _storageContract.ToString()), ("slot", slot)));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Data(result.GetProperty("value"), 32);
            McpAssert.Data(result.GetProperty("slot"), 32);
            Assert.That(result.GetProperty("address").GetString(), Is.EqualTo(_storageContract.ToString(true, true)));
            McpAssert.Quantity(result.GetProperty("blockNumber"), McpAssert.Hex(_node.Chain.BlockTree.Head!.Number));
            if (expectedValue is not null) Assert.That(result.GetProperty("value").GetString(), Is.EqualTo(expectedValue));
            if (expectedUint is not null) Assert.That(result.GetProperty("asUint").GetString(), Is.EqualTo(expectedUint));
            Assert.That(result.TryGetProperty("asAddress", out JsonElement asAddress), Is.EqualTo(hasAddress));
            if (slot == "1") Assert.That(asAddress.GetString(), Is.EqualTo(TestItem.AddressD.ToString(true, true)));
        }
    }

    [Test]
    public async Task Get_storage_at_before_deployment_is_zero()
    {
        JsonElement result = McpAssert.Success(await Call("get_storage_at", ("address", _storageContract.ToString()), ("slot", "0"), ("block", "earliest")));

        Assert.That(result.GetProperty("asUint").GetString(), Is.EqualTo("0"));
    }

    [Test]
    public async Task Estimate_gas_of_a_transfer_is_21000()
    {
        JsonElement result = McpAssert.Success(await Call("estimate_gas",
            ("from", TestItem.AddressB.ToString()), ("to", TestItem.AddressC.ToString()), ("value", "1000")));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(result.GetProperty("gas"), McpAssert.Hex(GasCostOf.Transaction));
            Assert.That(result.GetProperty("gasUnits").GetInt64(), Is.EqualTo(GasCostOf.Transaction));
        }
    }

    [Test]
    public async Task Estimate_gas_of_a_deployment_uses_init_code()
    {
        string initCode = Bytes.ToHexString(Prepare.EvmCode.ForInitOf(SeededChain.ReturnRuntimeCode).Done, true);

        JsonElement result = McpAssert.Success(await Call("estimate_gas", ("from", TestItem.AddressB.ToString()), ("data", initCode)));

        Assert.That(result.GetProperty("gasUnits").GetInt64(), Is.GreaterThan(GasCostOf.Transaction + GasCostOf.Create));
    }

    [Test]
    public async Task Estimate_gas_of_a_revert_returns_execution_reverted_with_reason()
    {
        JsonElement error = McpAssert.Error(
            await Call("estimate_gas", ("from", TestItem.AddressB.ToString()), ("to", _seeded.RevertContract.ToString()), ("data", "0x")),
            McpAssert.ExecutionReverted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("data").GetString(), Is.EqualTo(Bytes.ToHexString(SeededChain.RevertData, true)));
            Assert.That(error.GetProperty("reason").GetProperty("kind").GetString(), Is.Not.Empty);
            Assert.That(error.GetProperty("reason").GetProperty("message").GetString(), Is.Not.Empty);
            McpAssert.NoInternals(error);
        }
    }

    [Test]
    public async Task Estimate_gas_with_insufficient_balance_is_invalid_input()
    {
        JsonElement error = McpAssert.Error(await Call("estimate_gas",
            ("from", TestItem.GetRandomAddress().ToString()), ("to", TestItem.AddressC.ToString()), ("value", "1000000")), McpAssert.InvalidInput);

        McpAssert.NoInternals(error);
    }

    [Test]
    public async Task Get_proof_proves_account_and_storage()
    {
        JsonElement proof = McpAssert.Success(await Call("get_proof",
            ("address", _storageContract.ToString()), ("storageKeys", new[] { "0x0", "1" })));

        JsonElement storageProof = proof.GetProperty("storageProof");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.GetProperty("address").GetString(), Is.EqualTo(_storageContract.ToString()));
            Assert.That(proof.GetProperty("accountProof").GetArrayLength(), Is.GreaterThan(0));
            Assert.That(storageProof.GetArrayLength(), Is.EqualTo(2));
            Assert.That(storageProof.EnumerateArray().Select(static p => p.GetProperty("value").GetString()), Has.Member("0x2a"));
        }
    }

    [Test]
    public async Task Get_proof_without_storage_keys_proves_the_account()
    {
        JsonElement proof = McpAssert.Success(await Call("get_proof", ("address", TestItem.AddressC.ToString())));

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Quantity(proof.GetProperty("balance"), McpAssert.Hex(Core.Test.Blockchain.TestBlockchain.InitialValue + SeededChain.TransferValue));
            Assert.That(proof.GetProperty("storageProof").GetArrayLength(), Is.Zero);
        }
    }

    [TestCase(3, 0, 3, 3)]
    [TestCase(100, 0, 4, null)]
    [TestCase(2, 3, 1, null)]
    [TestCase(1, 4, 0, null)]
    public async Task Get_block_receipts_pages(int limit, int offset, int expectedCount, int? expectedNextOffset)
    {
        JsonElement page = McpAssert.Success(await Call("get_block_receipts",
            ("block", McpAssert.Hex(_seeded.Block.Number)), ("offset", offset), ("limit", limit)));

        JsonElement receipts = page.GetProperty("receipts");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.GetProperty("total").GetInt32(), Is.EqualTo(4));
            Assert.That(page.GetProperty("offset").GetInt32(), Is.EqualTo(offset));
            Assert.That(receipts.GetArrayLength(), Is.EqualTo(expectedCount));
            Assert.That(page.GetProperty("truncated").GetBoolean(), Is.EqualTo(expectedNextOffset is not null));
            Assert.That(page.TryGetProperty("nextOffset", out JsonElement next) ? next.GetInt32() : (int?)null, Is.EqualTo(expectedNextOffset));
            Assert.That(page.GetProperty("blockHash").GetString(), Is.EqualTo(_seeded.Block.Hash!.ToString()));
            for (int i = 0; i < expectedCount; i++)
            {
                Assert.That(receipts[i].GetProperty("transactionHash").GetString(), Is.EqualTo(_seeded.Block.Transactions[offset + i].Hash!.ToString()));
            }
        }
    }

    [Test]
    public async Task Get_block_receipts_by_hash_and_of_genesis()
    {
        JsonElement byHash = McpAssert.Success(await Call("get_block_receipts", ("block", _seeded.Block.Hash!.ToString())));
        JsonElement genesis = McpAssert.Success(await Call("get_block_receipts", ("block", "earliest")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byHash.GetProperty("total").GetInt32(), Is.EqualTo(4));
            Assert.That(genesis.GetProperty("total").GetInt32(), Is.Zero);
            Assert.That(genesis.GetProperty("truncated").GetBoolean(), Is.False);
        }
    }

    [Test]
    public async Task Get_block_receipts_page_respects_the_byte_budget()
    {
        await using McpTestNode small = await McpTestNode.Create(c => c.MaxResultSize = 2048);
        SeededChain seeded = await small.Seed();
        await using McpClient client = await small.CreateClient();

        JsonElement page = McpAssert.Success(await McpToolCalls.Call(client, "get_block_receipts", [("block", McpAssert.Hex(seeded.Block.Number))]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.GetProperty("receipts").GetArrayLength(), Is.InRange(1, 3));
            Assert.That(page.GetProperty("truncated").GetBoolean(), Is.True);
            Assert.That(page.GetProperty("nextOffset").GetInt32(), Is.EqualTo(page.GetProperty("receipts").GetArrayLength()));
        }
    }

    [Test]
    public async Task Fee_estimate_on_a_pre_london_chain_uses_legacy_prices()
    {
        JsonElement fees = McpAssert.Success(await Call("fee_estimate", ("blocks", 5)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fees.GetProperty("eip1559").GetBoolean(), Is.False);
            Assert.That(fees.GetProperty("nativeCurrency").GetString(), Is.EqualTo("ETH"));
            Assert.That(fees.GetProperty("blob").GetProperty("available").GetBoolean(), Is.False);
            Assert.That(fees.GetProperty("summary").GetString(), Does.Contain("legacy gasPrice"));
            McpAssert.Quantity(fees.GetProperty("suggestions").GetProperty("standard").GetProperty("maxFeePerGas"));
            Assert.That(fees.GetProperty("percentiles").EnumerateArray().Select(static p => p.GetDouble()), Is.EqualTo(new[] { 10.0, 50.0, 90.0 }));
        }
    }

    [Test]
    public async Task Fee_estimate_on_a_cancun_chain_reports_base_fee_suggestions()
    {
        await using McpTestNode cancun = await McpTestNode.Create(configureContainer: static b => b.AddSingleton<ISpecProvider>(new TestSpecProvider(Cancun.Instance)));
        await cancun.Chain.AddBlock();
        await cancun.Chain.AddBlock();
        await using McpClient client = await cancun.CreateClient();

        CallToolResult result = await McpToolCalls.Call(client, "fee_estimate", [("percentiles", new[] { 5.0, 25.0, 50.0, 75.0, 95.0 })]);
        JsonElement fees = McpAssert.Success(result);

        JsonElement suggestions = fees.GetProperty("suggestions");
        JsonElement blob = fees.GetProperty("blob");
        UInt256 nextBase = Wei(fees.GetProperty("baseFee").GetProperty("next"));
        UInt256 standardMax = Wei(suggestions.GetProperty("standard").GetProperty("maxFeePerGas"));
        UInt256 standardTip = Wei(suggestions.GetProperty("standard").GetProperty("maxPriorityFeePerGas"));
        bool blobsActive = cancun.Chain.BlockTree.Head!.Header.ExcessBlobGas is not null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fees.GetProperty("eip1559").GetBoolean(), Is.True);
            Assert.That(standardMax, Is.EqualTo(nextBase + nextBase + standardTip));
            Assert.That(suggestions.GetProperty("slow").GetProperty("percentile").GetDouble(), Is.EqualTo(5));
            Assert.That(suggestions.GetProperty("standard").GetProperty("percentile").GetDouble(), Is.EqualTo(50));
            Assert.That(suggestions.GetProperty("fast").GetProperty("percentile").GetDouble(), Is.EqualTo(95));
            Assert.That(blob.GetProperty("available").GetBoolean(), Is.EqualTo(blobsActive));
            if (blobsActive) McpAssert.Quantity(blob.GetProperty("nextBaseFeePerBlobGas"));
            Assert.That(fees.GetProperty("summary").GetString(), Does.Contain("next block base fee").And.Contain("ETH"));
        }

        McpClientTool tool = (await client.ListToolsAsync()).Single(static t => t.Name == "fee_estimate");
        McpSchemaValidator.AssertConforms(tool.ProtocolTool.OutputSchema!.Value, result.StructuredContent!.Value);
    }

    [Test]
    public async Task Gnosis_chain_reports_xdai()
    {
        await using McpTestNode gnosis = await McpTestNode.Create(configureContainer: static b =>
            b.AddSingleton<ISpecProvider>(new TestSpecProvider(London.Instance) { ChainId = BlockchainIds.Gnosis, NetworkId = BlockchainIds.Gnosis }));
        await using McpClient client = await gnosis.CreateClient();

        JsonElement info = McpAssert.Success(await McpToolCalls.Call(client, "chain_info", []));
        JsonElement balance = McpAssert.Success(await McpToolCalls.Call(client, "get_balance", [("address", TestItem.AddressA.ToString())]));
        JsonElement fees = McpAssert.Success(await McpToolCalls.Call(client, "fee_estimate", []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.GetProperty("nativeCurrency").GetString(), Is.EqualTo("xDAI"));
            Assert.That(info.GetProperty("isGnosisFamily").GetBoolean(), Is.True);
            Assert.That(info.GetProperty("chainIdDecimal").GetUInt64(), Is.EqualTo(BlockchainIds.Gnosis));
            Assert.That(balance.GetProperty("symbol").GetString(), Is.EqualTo("xDAI"));
            Assert.That(balance.GetProperty("balanceFormatted").GetString(), Is.EqualTo("1000"));
            Assert.That(fees.GetProperty("nativeCurrency").GetString(), Is.EqualTo("xDAI"));
            Assert.That(fees.GetProperty("transferCost").GetProperty("symbol").GetString(), Is.EqualTo("xDAI"));
            Assert.That(fees.GetProperty("summary").GetString(), Does.Contain("xDAI"));
        }
    }

    private static IEnumerable<TestCaseData> InvalidInputCases()
    {
        string contract = TestItem.AddressC.ToString();
        yield return Case("get_storage_at", "bad address", "address", "0x12", "slot", "0");
        yield return Case("get_storage_at", "negative slot", "address", contract, "slot", "-1");
        yield return Case("get_storage_at", "oversized slot", "address", contract, "slot", "0x1" + new string('0', 64));
        yield return Case("get_storage_at", "non numeric slot", "address", contract, "slot", "slot0");
        yield return Case("get_storage_at", "pending block", "address", contract, "slot", "0", "block", "pending");
        yield return Case("estimate_gas", "nothing to estimate", "from", contract);
        yield return Case("estimate_gas", "bad to", "to", "0x01");
        yield return Case("estimate_gas", "odd data", "to", contract, "data", "0x123");
        yield return Case("estimate_gas", "negative value", "to", contract, "value", "-5");
        yield return Case("get_proof", "bad address", "address", "0xzz");
        yield return Case("get_block_receipts", "pending block", "block", "pending");
    }

    [TestCaseSource(nameof(InvalidInputCases))]
    public async Task Invalid_input_is_rejected(string toolName, string[] args)
    {
        JsonElement error = McpAssert.Error(await Call(toolName, Pairs(args)), McpAssert.InvalidInput);
        McpAssert.NoInternals(error);
    }

    private static IEnumerable<TestCaseData> InvalidTypedInputCases()
    {
        yield return new TestCaseData("fee_estimate", new (string, object?)[] { ("blocks", 0) }).SetName("Invalid_fee_estimate_zero_blocks");
        yield return new TestCaseData("fee_estimate", new (string, object?)[] { ("blocks", 1025) }).SetName("Invalid_fee_estimate_too_many_blocks");
        yield return new TestCaseData("fee_estimate", new (string, object?)[] { ("percentiles", new[] { 90.0, 10.0 }) }).SetName("Invalid_fee_estimate_descending_percentiles");
        yield return new TestCaseData("fee_estimate", new (string, object?)[] { ("percentiles", new[] { 101.0 }) }).SetName("Invalid_fee_estimate_percentile_above_100");
        yield return new TestCaseData("fee_estimate", new (string, object?)[] { ("percentiles", Array.Empty<double>()) }).SetName("Invalid_fee_estimate_no_percentiles");
        yield return new TestCaseData("fee_estimate", new (string, object?)[] { ("percentiles", Enumerable.Range(0, 11).Select(static i => (double)i).ToArray()) }).SetName("Invalid_fee_estimate_too_many_percentiles");
        yield return new TestCaseData("get_proof", new (string, object?)[] { ("address", TestItem.AddressC.ToString()), ("storageKeys", Enumerable.Range(0, 65).Select(static i => i.ToString()).ToArray()) }).SetName("Invalid_get_proof_too_many_keys");
        yield return new TestCaseData("get_proof", new (string, object?)[] { ("address", TestItem.AddressC.ToString()), ("storageKeys", new[] { "0x0", "nope" }) }).SetName("Invalid_get_proof_bad_key");
        yield return new TestCaseData("get_block_receipts", new (string, object?)[] { ("block", "latest"), ("limit", 0) }).SetName("Invalid_get_block_receipts_zero_limit");
        yield return new TestCaseData("get_block_receipts", new (string, object?)[] { ("block", "latest"), ("limit", 1001) }).SetName("Invalid_get_block_receipts_limit_above_max");
        yield return new TestCaseData("get_block_receipts", new (string, object?)[] { ("block", "latest"), ("offset", -1) }).SetName("Invalid_get_block_receipts_negative_offset");
        yield return new TestCaseData("get_block_receipts", new (string, object?)[] { ("block", "latest"), ("offset", 50) }).SetName("Invalid_get_block_receipts_offset_beyond_total");
    }

    [TestCaseSource(nameof(InvalidTypedInputCases))]
    public async Task Invalid_typed_input_is_rejected(string toolName, (string, object?)[] args)
    {
        JsonElement error = McpAssert.Error(await Call(toolName, args), McpAssert.InvalidInput);
        McpAssert.NoInternals(error);
    }

    private static IEnumerable<TestCaseData> NotFoundCases()
    {
        yield return new TestCaseData("get_storage_at", new[] { "address", "{contract}", "slot", "0", "block", "0x100" }).SetName("Get_storage_at_beyond_head_is_not_found");
        yield return new TestCaseData("get_storage_at", new[] { "address", "{contract}", "slot", "0", "block", UnknownHash }).SetName("Get_storage_at_unknown_hash_is_not_found");
        yield return new TestCaseData("estimate_gas", new[] { "to", "{contract}", "block", "0x100" }).SetName("Estimate_gas_beyond_head_is_not_found");
        yield return new TestCaseData("get_proof", new[] { "address", "{contract}", "block", "0x100" }).SetName("Get_proof_beyond_head_is_not_found");
        yield return new TestCaseData("get_block_receipts", new[] { "block", "0x100" }).SetName("Get_block_receipts_beyond_head_is_not_found");
        yield return new TestCaseData("get_block_receipts", new[] { "block", UnknownHash }).SetName("Get_block_receipts_unknown_hash_is_not_found");
    }

    [TestCaseSource(nameof(NotFoundCases))]
    public async Task Missing_block_is_not_found(string toolName, string[] args)
    {
        (string Name, object? Value)[] pairs = Pairs(args);
        for (int i = 0; i < pairs.Length; i++)
        {
            if (Equals(pairs[i].Value, "{contract}")) pairs[i].Value = _storageContract.ToString();
        }

        JsonElement error = McpAssert.Error(await Call(toolName, pairs), McpAssert.NotFound);
        McpAssert.NoInternals(error);
    }

    private static IEnumerable<TestCaseData> SchemaCases()
    {
        yield return new TestCaseData("get_storage_at", new (string, object?)[] { ("address", "{contract}"), ("slot", "1") }).SetName("Output_schema_get_storage_at");
        yield return new TestCaseData("estimate_gas", new (string, object?)[] { ("to", "{contract}") }).SetName("Output_schema_estimate_gas");
        yield return new TestCaseData("fee_estimate", Array.Empty<(string, object?)>()).SetName("Output_schema_fee_estimate");
        yield return new TestCaseData("get_proof", new (string, object?)[] { ("address", "{contract}"), ("storageKeys", new[] { "0", BigSlot }) }).SetName("Output_schema_get_proof");
        yield return new TestCaseData("get_block_receipts", new (string, object?)[] { ("block", "latest"), ("limit", 1) }).SetName("Output_schema_get_block_receipts");
    }

    [TestCaseSource(nameof(SchemaCases))]
    public async Task Output_conforms_to_the_declared_schema(string toolName, (string Name, object? Value)[] args)
    {
        (string Name, object? Value)[] resolved = args.Select(a => (a.Name, Equals(a.Value, "{contract}") ? _storageContract.ToString() : a.Value)).ToArray();
        McpClientTool tool = (await _client.ListToolsAsync()).Single(t => t.Name == toolName);

        CallToolResult result = await Call(toolName, resolved);
        McpAssert.Success(result);

        Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null, $"{toolName} must declare an output schema");
        McpSchemaValidator.AssertConforms(tool.ProtocolTool.OutputSchema!.Value, result.StructuredContent!.Value);
    }

    /// <summary>Deploys a contract whose storage holds 42 at slot 0, <see cref="TestItem.AddressD"/> at slot 1 and 1 at <see cref="BigSlot"/>.</summary>
    private static async Task<Address> DeployStorageContract(McpTestNode node)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        byte[] initCode = Prepare.EvmCode
            .PersistData("0x00", "0x2a")
            .PersistData("0x01", TestItem.AddressD.ToString())
            .PersistData(BigSlot, "0x01")
            .ForInitOf([(byte)Instruction.STOP])
            .Done;
        Transaction deploy = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(1)
            .WithCode(initCode).WithGasLimit(300_000).SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
        Block block = await node.Chain.AddBlock(deploy);
        Assert.That(block.Transactions, Has.Length.EqualTo(1), "precondition: the storage contract must be deployed");
        return ContractAddress.From(sender.Address, nonce);
    }

    private static UInt256 Wei(JsonElement value)
    {
        Assert.That(Plugin.Tools.McpToolInput.TryParseUInt256(value.GetString(), "wei", out UInt256 wei, out string? error), Is.True, error);
        return wei;
    }

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
}

/// <summary>
/// A minimal JSON Schema checker for tool output schemas: <c>type</c> (single or list), <c>enum</c>, <c>required</c>,
/// <c>properties</c>, <c>items</c> and local <c>$ref</c>s to <c>#/$defs/...</c>.
/// </summary>
internal static class McpSchemaValidator
{
    public static void AssertConforms(JsonElement schema, JsonElement value)
    {
        List<string> errors = [];
        Validate(schema, schema, value, "$", errors);
        Assert.That(errors, Is.Empty, () => $"output does not match its schema:\n{string.Join("\n", errors)}\nvalue: {value}");
    }

    private static void Validate(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string target = reference.GetString()!;
            Assert.That(target, Does.StartWith("#/$defs/"), "only local $defs references are supported");
            schema = root.GetProperty("$defs").GetProperty(target["#/$defs/".Length..]);
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            IEnumerable<string> allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(static t => t.GetString()!)
                : [type.GetString()!];
            if (!allowed.Any(t => Matches(t, value)))
            {
                errors.Add($"{path}: expected {type}, got {value.ValueKind} ({Truncate(value)})");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out JsonElement options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            errors.Add($"{path}: {value} is not one of {options}");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement name in required.EnumerateArray())
                {
                    if (!value.TryGetProperty(name.GetString()!, out _)) errors.Add($"{path}: missing required property '{name.GetString()}'");
                }
            }

            if (schema.TryGetProperty("properties", out JsonElement properties))
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                    {
                        Validate(root, propertySchema, property.Value, $"{path}.{property.Name}", errors);
                    }
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int i = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                Validate(root, items, item, $"{path}[{i++}]", errors);
            }
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new NotSupportedException($"schema type '{type}'")
    };

    private static string Truncate(JsonElement value)
    {
        string text = value.ToString();
        return text.Length > 80 ? text[..80] + "..." : text;
    }
}
