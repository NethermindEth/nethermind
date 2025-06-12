// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private static readonly byte[] InfiniteLoopCode = Prepare.EvmCode
        .Op(Instruction.JUMPDEST)
        .PushData(0)
        .Op(Instruction.JUMP)
        .Done;

    [Test]
    public async Task Eth_call_web3_sample()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_web3_sample_not_enough_gas_system_account()
    {
        using Context ctx = await Context.Create();
        ctx.Test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
    }

    [Test]
    public async Task Eth_call_web3_should_return_insufficient_balance_error()
    {
        using Context ctx = await Context.Create();
        Address someAccount = new("0x0001020304050607080910111213141516171819");
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"value\": 500, \"gas\": 100000000}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"err: insufficient funds for transfer: address 0x0001020304050607080910111213141516171819 (supplied gas 100000000)\"},\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
    }

    [Test]
    public async Task Eth_call_web3_sample_not_enough_gas_other_account()
    {
        using Context ctx = await Context.Create();
        Address someAccount = new("0x0001020304050607080910111213141516171819");
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
        ctx.Test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
    }

    [Test]
    public async Task Eth_call_no_sender()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), 1, Keccak.Zero, 1L)
        {
            To = TestItem.AddressB
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_no_recipient_should_work_as_init()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), 1, Keccak.Zero, 1L)
        {
            From = TestItem.AddressA,
            Input = [1, 2, 3],
            Gas = 100000000
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32015,\"message\":\"VM execution error.\",\"data\":\"err: StackUnderflow (supplied gas 100000000)\"},\"id\":67}"));
    }


    [Test]
    public async Task should_not_reject_transactions_with_deployed_code_when_eip3607_enabled()
    {
        OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1, IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(releaseSpec) { AllowTestChainOverride = false };
        using Context ctx = await Context.Create(specProvider);

        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        LegacyTransactionForRpc transaction = new(tx, 1, Keccak.Zero, 1L)
        {
            To = TestItem.AddressB
        };
        ctx.Test.WorldStateManager.GlobalWorldState.InsertCode(TestItem.AddressA, "H"u8.ToArray(), London.Instance);

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_ethereum_recipient()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_call",
            "{\"data\":\"0x12\",\"from\":\"0x7301cfa0e1756b71869e93d4e4dca5c7d0eb0aa6\",\"to\":\"ethereum\"}",
            "latest");
        Assert.That(serialized.StartsWith("{\"jsonrpc\":\"2.0\",\"error\""), Is.True);
    }

    [Test]
    public async Task Eth_call_ok()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), 1, Keccak.Zero, 1L)
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_blockhash_ok()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), 1, Keccak.Zero, 1L)
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "{\"blockHash\":\"0xf0b3f69cbd4e1e8d9b0ef02ff5d1384d18e19d251a4052f5f90bab190c5e8937\"}");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"0xf0b3f69cbd4e1e8d9b0ef02ff5d1384d18e19d251a4052f5f90bab190c5e8937 could not be found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_create_tx_with_empty_data()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), 1, Keccak.Zero, 1L)
        {
            From = TestItem.AddressA
        };
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        serialized.Should().BeEquivalentTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"Contract creation without any data provided.\"},\"id\":67}");
    }

    [Test]
    public async Task Eth_call_missing_state_after_fast_sync()
    {
        using Context ctx = await Context.Create(configurer: builder => builder.ConfigureTrieStoreExposedWorldStateManager());
        LegacyTransactionForRpc transaction = new(new Transaction(), 1, Keccak.Zero, 1L)
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB
        };

        ctx.Test.StateDb.Clear();
        ctx.Test.Container.Resolve<TrieStore>().ClearCache();

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        serialized.Should().StartWith("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,");
    }

    [Test]
    public async Task Eth_call_with_accessList()
    {
        var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListForRpc accessList) = GetTestAccessList();

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");

        transaction.AccessList = accessList;
        string serialized = await test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x010203\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_without_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_without_gas_pricing_after_1559_legacy()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_without_gas_pricing_after_1559_new_type_of_transaction()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
        byte[] code = Prepare.EvmCode
            .Op(Instruction.BASEFEE)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;
    }

    [Test]
    public async Task Eth_call_with_base_fee_opcode_should_return_0()
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
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_revert()
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
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32015,\"message\":\"VM execution error.\",\"data\":\"err: revert (supplied gas 100000000)\"},\"id\":67}"));
    }

    [TestCase(
        "Nonce override doesn't cause failure",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"nonce":"0x123"}}""",
        """{"jsonrpc":"2.0","result":"0x","id":67}"""
    )]
    [TestCase(
        "Uses account balance from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","value":"0x100"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}""",
        """{"jsonrpc":"2.0","result":"0x","id":67}"""
    )]
    [TestCase(
        "Executes code from state override",
        """{"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}""",
        """{"jsonrpc":"2.0","result":"0x00000000000000000000000000000000000000000000003635c9adc5de9f09e5","id":67}"""
    )]
    [TestCase(
        "Executes precompile using overriden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        """{"jsonrpc":"2.0","result":"0x000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099","id":67}"""
    )]
    public async Task Eth_call_with_state_override(string name, string transactionJson, string stateOverrideJson, string expectedResult)
    {
        var transaction = JsonSerializer.Deserialize<object>(transactionJson);
        var stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        JToken.Parse(serialized).Should().BeEquivalentTo(expectedResult);
    }

    [TestCase(
        "When balance and nonce is overriden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f","value":"0x1"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x123", "nonce": "0x123"}}"""
    )]
    [TestCase(
        "When address code is overriden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}"""
    )]
    [TestCase(
        "When precompile address is changed",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}"""
    )]
    public async Task Eth_call_with_state_override_does_not_affect_other_calls(string name, string transactionJson, string stateOverrideJson)
    {
        var transaction = JsonSerializer.Deserialize<object>(transactionJson);
        var stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        var resultOverrideBefore = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        var resultNoOverride = await ctx.Test.TestEthRpc("eth_call", transaction, "latest");

        var resultOverrideAfter = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        using (new AssertionScope())
        {
            JToken.Parse(resultOverrideBefore).Should().BeEquivalentTo(resultOverrideAfter);
            JToken.Parse(resultNoOverride).Should().NotBeEquivalentTo(resultOverrideAfter);
        }
    }

    [Test]
    public async Task Eth_call_uses_block_gas_limit_when_not_specified()
    {
        using Context ctx = await Context.Create();

        // Get the current block's gas limit
        string blockNumberResponse = await ctx.Test.TestEthRpc("eth_blockNumber");
        string blockNumber = JToken.Parse(blockNumberResponse).Value<string>("result")!;
        string blockResponse = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockNumber, false);
        long blockGasLimit = Convert.ToInt64(JToken.Parse(blockResponse).SelectToken("result.gasLimit")!.Value<string>(), 16);

        await TestEthCallOutOfGas(ctx, null, blockGasLimit);
    }

    [Test]
    public async Task Eth_call_uses_specified_gas_limit()
    {
        using Context ctx = await Context.Create();
        await TestEthCallOutOfGas(ctx, 30000000, 30000000);
    }

    [Test]
    public async Task Eth_call_cannot_exceed_gas_cap()
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.GasCap = 50000000;
        await TestEthCallOutOfGas(ctx, 300000000, 50000000);
    }

    private static async Task TestEthCallOutOfGas(Context ctx, long? specifiedGasLimit, long expectedGasLimit)
    {
        string gasParam = specifiedGasLimit.HasValue ? $", \"gas\": \"0x{specifiedGasLimit.Value:X}\"" : "";
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\"{gasParam}, \"data\": \"{InfiniteLoopCode.ToHexString()}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        JToken.Parse(serialized).Should().BeEquivalentTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32015,\"message\":\"VM execution error.\",\"data\":\"err: OutOfGas (supplied gas {expectedGasLimit})\"}},\"id\":67}}");
    }
}
