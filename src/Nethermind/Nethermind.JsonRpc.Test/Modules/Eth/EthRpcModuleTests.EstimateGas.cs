// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_estimateGas_web3_should_return_insufficient_balance_error()
    {
        using Context ctx = await Context.Create();
        Address someAccount = new("0x0001020304050607080910111213141516171819");
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"value\": 500}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"err: insufficient funds for transfer: address 0x0001020304050607080910111213141516171819 (supplied gas 4000000)\"},\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
    }


    [Test]
    public async Task Eth_estimateGas_web3_sample_not_enough_gas_system_account()
    {
        using Context ctx = await Context.Create();
        ctx.Test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
    }

    [Test]
    public async Task Eth_estimateGas_web3_sample_not_enough_gas_other_account()
    {
        using Context ctx = await Context.Create();
        Address someAccount = new("0x0001020304050607080910111213141516171819");
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
    }

    [Test]
    public async Task Eth_estimateGas_web3_above_block_gas_limit()
    {
        using Context ctx = await Context.Create();
        Address someAccount = new("0x0001020304050607080910111213141516171819");
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gas\":\"0x100000000\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
    }

    [TestCase(false, 2)]
    [TestCase(true, 2)]
    [TestCase(true, 17)]
    public async Task Eth_create_access_list_calculates_proper_gas(bool optimize, long loads)
    {
        var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, _) = GetTestAccessList(loads);

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
        string serializedCreateAccessList = await test.TestEthRpc("eth_createAccessList",
            transaction, "0x0", optimize.ToString().ToLower());

        transaction.AccessList = test.JsonSerializer.Deserialize<AccessListForRpc>(JToken.Parse(serializedCreateAccessList).SelectToken("result.accessList")!.ToString());
        string serializedEstimateGas =
            await test.TestEthRpc("eth_estimateGas", transaction, "0x0");

        var gasUsedEstimateGas = JToken.Parse(serializedEstimateGas).Value<string>("result");
        var gasUsedCreateAccessList =
            JToken.Parse(serializedCreateAccessList).SelectToken("result.gasUsed")?.Value<string>();

        var gasUsedAccessList = (long)Bytes.FromHexString(gasUsedCreateAccessList!).ToUInt256();
        var gasUsedEstimate = (long)Bytes.FromHexString(gasUsedEstimateGas!).ToUInt256();
        Assert.That(gasUsedEstimate, Is.EqualTo((double)gasUsedAccessList).Within(1.5).Percent);
    }

    [TestCase(true, 0xeee7, 0xf71b)]
    [TestCase(false, 0xeee7, 0xee83)]
    public async Task Eth_estimate_gas_with_accessList(bool senderAccessList, long gasPriceWithoutAccessList,
        long gasPriceWithAccessList)
    {
        var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithConfig(new JsonRpcConfig() { EstimateErrorMargin = 0 })
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListForRpc accessList) = GetTestAccessList(2, senderAccessList);

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
        string serialized = await test.TestEthRpc("eth_estimateGas", transaction, "0x0");
        Assert.That(
            serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{gasPriceWithoutAccessList.ToHexString(true)}\",\"id\":67}}"));

        transaction.AccessList = accessList;
        serialized = await test.TestEthRpc("eth_estimateGas", transaction, "0x0");
        Assert.That(
            serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{gasPriceWithAccessList.ToHexString(true)}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_estimate_gas_is_lower_with_optimized_access_list()
    {
        var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListForRpc accessList) = GetTestAccessList(2, true);
        (byte[] _, AccessListForRpc optimizedAccessList) = GetTestAccessList(2, false);

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
        transaction.AccessList = accessList;
        string serialized = await test.TestEthRpc("eth_estimateGas", transaction, "0x0");
        long estimateGas = Convert.ToInt64(JToken.Parse(serialized).Value<string>("result"), 16);

        transaction.AccessList = optimizedAccessList;
        serialized = await test.TestEthRpc("eth_estimateGas", transaction, "0x0");
        long optimizedEstimateGas = Convert.ToInt64(JToken.Parse(serialized).Value<string>("result"), 16);

        optimizedEstimateGas.Should().BeLessThan(estimateGas);
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing_after_1559_legacy()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing_after_1559_new_type_of_transaction()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
        byte[] code = Prepare.EvmCode
            .Op(Instruction.BASEFEE)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;
    }

    [Test]
    public async Task Estimate_gas_with_base_fee_opcode()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        byte[] code = Prepare.EvmCode
            .Op(Instruction.BASEFEE)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData("0x20")
            .PushData("0x0")
            .Op(Instruction.RETURN)
            .Done;

        string dataStr = code.ToHexString();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\", \"data\": \"{dataStr}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xe891\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_revert()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        string dataStr = code.ToHexString();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\", \"data\": \"{dataStr}\", \"gas\": 100000000}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32015,\"message\":\"err: revert (supplied gas 100000000)\"},\"id\":67}"));
    }

    [Test]
    public async Task should_estimate_transaction_with_deployed_code_when_eip3607_enabled()
    {
        OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1, IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(releaseSpec) { AllowTestChainOverride = false };
        using Context ctx = await Context.Create(specProvider);

        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        LegacyTransactionForRpc transaction = new LegacyTransactionForRpc(tx, 1, Keccak.Zero, 1L)
        {
            To = TestItem.AddressB
        };
        ctx.Test.WorldStateManager.GlobalWorldState.InsertCode(TestItem.AddressA, "H"u8.ToArray(), London.Instance);

        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [TestCase(
        "Nonce override doesn't cause failure",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"nonce":"0x123"}}""",
        """{"jsonrpc":"2.0","result":"0x5208","id":67}""" // ETH transfer (intrinsic transaction cost)
    )]
    [TestCase(
        "Uses account balance from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","value":"0x100"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}""",
        """{"jsonrpc":"2.0","result":"0x5208","id":67}""" // ETH transfer (intrinsic transaction cost)
    )]
    [TestCase(
        "Executes code from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0x60fe47b1112233445566778899001122334455667788990011223344556677889900112233445566778899001122"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x6080604052348015600e575f80fd5b50600436106030575f3560e01c80632a1afcd914603457806360fe47b114604d575b5f80fd5b603b5f5481565b60405190815260200160405180910390f35b605c6058366004605e565b5f55565b005b5f60208284031215606d575f80fd5b503591905056fea2646970667358221220fd4e5f3894be8e57fc7460afebb5c90d96c3486d79bf47b00c2ed666ab2f82b364736f6c634300081a0033"}}""",
        """{"jsonrpc":"2.0","result":"0xabdd","id":67}""" // Store uint256 (cold access) + few other light instructions + intrinsic transaction cost
    )]
    [TestCase(
        "Executes precompile using overriden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        """{"jsonrpc":"2.0","result":"0x6440","id":67}""" // EcRecover call + intrinsic transaction cost
    )]
    public async Task Estimate_gas_with_state_override(string name, string transactionJson, string stateOverrideJson, string expectedResult)
    {
        var transaction = JsonSerializer.Deserialize<object>(transactionJson);
        var stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        TestSpecProvider specProvider = new(Prague.Instance);
        using Context ctx = await Context.Create(specProvider);

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        JToken.Parse(serialized).Should().BeEquivalentTo(expectedResult);
    }

    [TestCase(
        "When balance and nonce is overriden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","value":"0x123"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x123", "nonce": "0x123"}}"""
    )]
    [TestCase(
        "When address code is overriden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0x60fe47b1112233445566778899001122334455667788990011223344556677889900112233445566778899001122"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x6080604052348015600e575f80fd5b50600436106030575f3560e01c80632a1afcd914603457806360fe47b114604d575b5f80fd5b603b5f5481565b60405190815260200160405180910390f35b605c6058366004605e565b5f55565b005b5f60208284031215606d575f80fd5b503591905056fea2646970667358221220fd4e5f3894be8e57fc7460afebb5c90d96c3486d79bf47b00c2ed666ab2f82b364736f6c634300081a0033"}}"""
    )]
    [TestCase(
        "When precompile address is changed",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}"""
    )]
    public async Task Estimate_gas_with_state_override_does_not_affect_other_calls(string name, string transactionJson, string stateOverrideJson)
    {
        var transaction = JsonSerializer.Deserialize<object>(transactionJson);
        var stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        var resultOverrideBefore = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        var resultNoOverride = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest");

        var resultOverrideAfter = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        using (new AssertionScope())
        {
            JToken.Parse(resultOverrideBefore).Should().BeEquivalentTo(resultOverrideAfter);
            JToken.Parse(resultNoOverride).Should().NotBeEquivalentTo(resultOverrideAfter);
        }
    }

    [Test]
    public async Task Estimate_gas_uses_block_gas_limit_when_not_specified()
    {
        using Context ctx = await Context.Create();

        string blockNumberResponse = await ctx.Test.TestEthRpc("eth_blockNumber");
        string blockNumber = JToken.Parse(blockNumberResponse).Value<string>("result")!;
        string blockResponse = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockNumber, false);
        long blockGasLimit = Convert.ToInt64(JToken.Parse(blockResponse).SelectToken("result.gasLimit")!.Value<string>(), 16);

        await TestEstimateGasOutOfGas(ctx, null, blockGasLimit);
    }

    [Test]
    public async Task Estimate_gas_uses_specified_gas_limit()
    {
        using Context ctx = await Context.Create();
        await TestEstimateGasOutOfGas(ctx, 30000000, 30000000);
    }

    [Test]
    public async Task Estimate_gas_cannot_exceed_gas_cap()
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.GasCap = 50000000;
        await TestEstimateGasOutOfGas(ctx, 300000000, 50000000);
    }

    private static async Task TestEstimateGasOutOfGas(Context ctx, long? specifiedGasLimit, long expectedGasLimit)
    {
        string gasParam = specifiedGasLimit.HasValue ? $", \"gas\": \"0x{specifiedGasLimit.Value:X}\"" : "";
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\"{gasParam}, \"data\": \"{InfiniteLoopCode.ToHexString()}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        JToken.Parse(serialized).Should().BeEquivalentTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32015,\"message\":\"err: OutOfGas (supplied gas {expectedGasLimit})\"}},\"id\":67}}");
    }

}
