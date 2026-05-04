// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Init;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Int256;
using Nethermind.Core.Specs;
using Nethermind.Blockchain;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Nethermind.Abi;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_call_web3_sample()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_web3_sample_not_enough_gas_system_account()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, Address.SystemUser);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
        AssertAccountDoesNotExist(ctx, Address.SystemUser);
    }

    [Test]
    public async Task Eth_call_web3_should_return_insufficient_balance_error()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, TestAccount);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestAccountAddress}\",\"gasPrice\":\"0x100000\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\", \"value\": 500, \"gas\": 1000000}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        JToken parsed = JToken.Parse(serialized);
        parsed["error"]!["code"]!.Value<int>().Should().Be(-32003);
        parsed["error"]!["message"]!.Value<string>().Should().Contain("insufficient funds");
        AssertAccountDoesNotExist(ctx, TestAccount);
    }

    [Test]
    public async Task Eth_call_web3_sample_not_enough_gas_other_account()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, TestAccount);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestAccountAddress}\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "0x0");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
        AssertAccountDoesNotExist(ctx, TestAccount);
    }

    [Test]
    public async Task Eth_call_no_sender()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), new(BlockchainIds.Mainnet))
        {
            To = TestItem.AddressB,
            Gas = 1000000
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_no_recipient_should_work_as_init()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), new(BlockchainIds.Mainnet))
        {
            From = TestItem.AddressA,
            Input = [1, 2, 3],
            Gas = 1000000
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32003,\"message\":\"stack underflow\"},\"id\":67}"));
    }


    [Test]
    public async Task should_not_reject_transactions_with_deployed_code_when_eip3607_enabled()
    {
        OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1, IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(releaseSpec) { AllowTestChainOverride = false };
        using Context ctx = await Context.Create(specProvider, configurer: builder => builder
            .WithGenesisPostProcessor((block, state) =>
            {
                state.InsertCode(TestItem.AddressA, "H"u8.ToArray(), London.Instance);
            }));

        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        LegacyTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet))
        {
            To = TestItem.AddressB,
            GasPrice = 0
        };

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
        LegacyTransactionForRpc transaction = new(new Transaction(), new(BlockchainIds.Mainnet))
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            Gas = 1000000
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_blockhash_ok()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), new(BlockchainIds.Mainnet))
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB
        };

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "{\"blockHash\":\"0xf0b3f69cbd4e1e8d9b0ef02ff5d1384d18e19d251a4052f5f90bab190c5e8937\"}");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"header not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_create_tx_with_empty_data()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), new(BlockchainIds.Mainnet))
        {
            From = TestItem.AddressA
        };
        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        serialized.Should().Be("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"contract creation without any data provided\"},\"id\":67}");
    }

    [Test]
    public async Task Eth_call_missing_state_after_fast_sync()
    {
        using Context ctx = await Context.Create();
        LegacyTransactionForRpc transaction = new(new Transaction(), new(BlockchainIds.Mainnet))
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB
        };

        ctx.Test.Container.Resolve<MainPruningTrieStoreFactory>().PruningTrieStore.PersistCache(CancellationToken.None);
        ctx.Test.StateDb.Clear();

        string serialized =
            await ctx.Test.TestEthRpc("eth_call", transaction, "latest");
        serialized.Should().StartWith("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,");
    }

    [Test]
    public async Task Eth_call_with_accessList()
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
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
            $"{{\"from\": \"{BatTokenAddress}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x10\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_without_gas_pricing_after_1559_legacy()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x100000000\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_without_gas_pricing_after_1559_new_type_of_transaction()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"to\": \"{SecondaryTestAddress}\", \"type\": \"0x2\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_base_fee_opcode_should_return_0()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string dataStr = BaseFeeReturnCode.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"type\": \"0x2\", \"data\": \"{dataStr}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_base_fee_opcode_without_from_address_should_return_0()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string dataStr = BaseFeeReturnCode.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"type\": \"0x2\", \"data\": \"{dataStr}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_with_coinbase_opcode_should_return_block_override_fee_recipient()
    {
        using Context ctx = await Context.Create();

        string dataStr = CoinbaseReturnCode.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"data\": \"{dataStr}\"}}");
        object? blockOverride = JsonSerializer.Deserialize<object>(
            $"{{\"feeRecipient\":\"{TestItem.AddressC}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", null, blockOverride);

        JToken.Parse(serialized).Value<string>("result")!
            .Should().Be($"0x{new string('0', 24)}{TestItem.AddressC.Bytes.ToHexString()}");
    }

    [Test]
    public async Task Eth_call_with_value_transfer_without_from_address_should_throw()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string dataStr = BaseFeeReturnCode.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"type\": \"0x2\", \"value\":\"{1.Ether}\", \"data\": \"{dataStr}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        JToken parsed = JToken.Parse(serialized);
        parsed["error"]!["code"]!.Value<int>().Should().Be(-32003);
        parsed["error"]!["message"]!.Value<string>().Should().Contain("insufficient funds");
    }

    [Test]
    public async Task Eth_call_with_revert()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        AbiEncoder abiEncoder = new();
        AbiSignature errorSignature = new(
            "Error",
            AbiType.String
        );
        string errorMessage = "wrong-parameters";
        byte[] encodedError = abiEncoder.Encode(
            AbiEncodingStyle.IncludeSignature,  // Include the 0x08c379a0 selector
            errorSignature,
            errorMessage
        );
        string abiEncodedErrorMessage = encodedError.ToHexString(true);

        byte[] code = Prepare.EvmCode
            .RevertWithSolidityErrorEncoding(errorMessage)
            .Done;

        string dataStr = code.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted: {{errorMessage}}","data":"{{abiEncodedErrorMessage}}"},"id":67}"""));
    }

    [Test]
    public async Task Eth_call_with_custom_error_revert_puts_bytes_only_in_data()
    {
        // When a contract reverts with a custom error (unknown 4-byte selector), message must be
        // plain "execution reverted" and the raw bytes must appear only in data — matching Geth.
        // See: https://github.com/NethermindEth/nethermind/issues/11095
        using Context ctx = await Context.CreateWithLondonEnabled();

        byte[] selector = Keccak.Compute("ActionFailed()").Bytes[..4].ToArray();

        byte[] code = Prepare.EvmCode
            .RevertWithCustomError(selector)
            .Done;

        string dataStr = code.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo("""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"0x080a1c27"},"id":67}"""));
    }

    [Test]
    public async Task Eth_call_with_revert_sentinel_string_as_message_still_appends_decoded_message()
    {
        // require(false, "revert") produces Error(string) ABI-encoded with the literal string "revert".
        // The old sentinel-based check would have mistaken this for the Revert sentinel and emitted
        // plain "execution reverted". The raw-byte prefix check fixes this: we see the Error(string)
        // selector in the revert data, so we correctly emit "execution reverted: revert".
        using Context ctx = await Context.CreateWithLondonEnabled();

        AbiEncoder abiEncoder = new();
        AbiSignature errorSignature = new("Error", AbiType.String);
        string errorMessage = "revert"; // deliberately equals the sentinel constant
        byte[] encodedError = abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, errorSignature, errorMessage);
        string abiEncodedErrorMessage = encodedError.ToHexString(true);

        byte[] code = Prepare.EvmCode
            .RevertWithSolidityErrorEncoding(errorMessage)
            .Done;

        string dataStr = code.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        Assert.That(
            serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted: revert","data":"{{abiEncodedErrorMessage}}"},"id":67}"""));
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
        "Executes precompile using overridden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        """{"jsonrpc":"2.0","result":"0x000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099","id":67}"""
    )]
    public async Task Eth_call_with_state_override(string name, string transactionJson, string stateOverrideJson, string expectedResult)
    {
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        JToken.Parse(serialized).Should().BeEquivalentTo(expectedResult);
    }

    [TestCase(
        "When balance and nonce is overridden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f","value":"0x1"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x123", "nonce": "0x123"}}"""
    )]
    [TestCase(
        "When address code is overridden",
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
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        string resultOverrideBefore = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        string resultNoOverride = await ctx.Test.TestEthRpc("eth_call", transaction, "latest");

        string resultOverrideAfter = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        using (new AssertionScope())
        {
            JToken.Parse(resultOverrideBefore).Should().BeEquivalentTo(resultOverrideAfter);
            JToken.Parse(resultNoOverride).Should().NotBeEquivalentTo(resultOverrideAfter);
        }
    }

    [Test]
    public async Task Eth_call_uses_gas_cap_when_not_specified()
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.GasCap = 5_000_000;

        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"data\": \"{InfiniteLoopCode.ToHexString(true)}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        JToken.Parse(serialized).Should().BeEquivalentTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32003,\"message\":\"out of gas\"}},\"id\":67}}");
    }

    [Test]
    public async Task Eth_call_uses_specified_gas_limit()
    {
        using Context ctx = await Context.Create();
        await TestEthCallOutOfGas(ctx, 30000000);
    }

    [Test]
    public async Task Eth_call_cannot_exceed_gas_cap()
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.GasCap = 50000000;
        await TestEthCallOutOfGas(ctx, 300000000);
    }

    /// <summary>
    /// Verifies gas cap is enforced by measuring actual gas available to the EVM.
    /// Gas cap enforcement is in the shared <c>TxExecutor.Execute()</c> base class,
    /// so this also covers <c>eth_estimateGas</c> and <c>eth_createAccessList</c>.
    /// </summary>
    [Test]
    public async Task Eth_call_gas_available_is_capped_by_gas_cap()
    {
        using Context ctx = await Context.Create();
        long gasCap = 50_000;
        ctx.Test.RpcConfig.GasCap = gasCap;

        // Contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
        // Returns gas available at start of execution as a 32-byte uint256
        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xc200000000000000000000000000000000000000":{"code":"0x5a60005260206000f3"}}""");

        // Request 100K gas — should be capped to 50K by GasCap
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            """{"to":"0xc200000000000000000000000000000000000000", "gas":"0x186A0"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        string result = JToken.Parse(serialized).Value<string>("result")!;
        long gasAvailable = (long)Bytes.FromHexString(result).ToUInt256();

        // gas available = gasLimit - intrinsicGas; if gas cap works, gasLimit ≤ 50K so gas available < 50K
        // Without gas cap, gas available would be ~79K (100K - 21K intrinsic)
        gasAvailable.Should().BeLessThan(gasCap);
        gasAvailable.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Regression test for: when no gas is specified, Nethermind must default to gasCap, not the block gas
    /// limit. On chains where gasCap &gt; blockGasLimit (e.g. Gnosis: gasCap=600M, blockGasLimit=17M),
    /// calls that need more gas than the block limit were silently under-executing and returning wrong values.
    /// </summary>
    [Test]
    public async Task Eth_call_without_gas_defaults_to_gas_cap_not_block_gas_limit()
    {
        using Context ctx = await Context.Create();

        string blockNumberResponse = await ctx.Test.TestEthRpc("eth_blockNumber");
        string blockNumber = JToken.Parse(blockNumberResponse).Value<string>("result")!;
        string blockResponse = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockNumber, false);
        long blockGasLimit = Convert.ToInt64(JToken.Parse(blockResponse).SelectToken("result.gasLimit")!.Value<string>(), 16);

        long gasCap = blockGasLimit * 10;
        ctx.Test.RpcConfig.GasCap = gasCap;

        // Contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
        // Returns the gas available at the start of execution as a uint256.
        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xc200000000000000000000000000000000000000":{"code":"0x5a60005260206000f3"}}""");

        // No gas field — should default to gasCap, not blockGasLimit.
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            """{"to":"0xc200000000000000000000000000000000000000"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride);

        string result = JToken.Parse(serialized).Value<string>("result")!;
        UInt256 gasAvailable = Bytes.FromHexString(result).ToUInt256();

        // With the bug: gas available ≈ blockGasLimit - intrinsicGas < blockGasLimit
        // With the fix: gas available ≈ gasCap - intrinsicGas > blockGasLimit
        gasAvailable.Should().BeGreaterThan((UInt256)blockGasLimit,
            "gas available ({0}) should reflect gasCap ({1}), not block gas limit ({2})",
            gasAvailable, gasCap, blockGasLimit);
    }

    [Test]
    public async Task Eth_call_ignores_invalid_nonce()
    {
        using Context ctx = await Context.Create();
        byte[] code = Prepare.EvmCode
         .Op(Instruction.STOP)
         .Done;
        Transaction tx = Build.A.Transaction
            .WithNonce(123)
            .WithGasLimit(100000)
            .WithData(code)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = null;

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_contract_creation()
    {
        using Context ctx = await Context.Create();
        byte[] code = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        Transaction tx = Build.A.Transaction
            .WithData(code)
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        LegacyTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.To = null;
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [TestCase(null)]
    [TestCase(new byte[0])]
    public async Task Eth_call_to_is_null_and_not_contract_creation(byte[]? data)
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        LegacyTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.To = null;
        transaction.Data = data;
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"contract creation without any data provided\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_gas_price_in_eip1559_tx()
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction
            .WithGasLimit(100000)
            .To(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = new UInt256(1);
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"both gasPrice and (maxFeePerGas or maxPriorityFeePerGas) specified\"},\"id\":67}"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Eth_call_no_blobs_in_blob_tx(bool isNull)
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction
            .WithGasLimit(100000)
            .WithBlobVersionedHashes(isNull ? null : [])
            .To(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        BlobTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = null;
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"need at least 1 blob for a blob transaction\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_maxFeePerBlobGas_is_zero()
    {
        using Context ctx = await Context.Create();
        byte[] validHash = new byte[32];
        validHash[0] = 0x01; // KZG version
        Transaction tx = Build.A.Transaction
            .WithGasLimit(100000)
            .WithBlobVersionedHashes([validHash])
            .To(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        BlobTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.MaxFeePerBlobGas = 0;
        transaction.GasPrice = null;
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"maxFeePerBlobGas, if specified, must be non-zero\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_missing_to_in_blob_tx()
    {
        using Context ctx = await Context.Create();
        byte[] code = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] validHash = new byte[32];
        validHash[0] = 0x01; // KZG version
        Transaction tx = Build.A.Transaction
            .WithData(code)
            .WithGasLimit(100000)
            .WithMaxFeePerBlobGas(1)
            .WithBlobVersionedHashes([validHash])
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        BlobTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.To = null;
        transaction.GasPrice = null;
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"missing \\\"to\\\" in blob transaction\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_call_maxFeePerGas_smaller_then_maxPriorityFeePerGas()
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet))
        {
            MaxFeePerGas = 1,
            MaxPriorityFeePerGas = 2,
            GasPrice = null
        };

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"maxFeePerGas (1) < maxPriorityFeePerGas (2)\"},\"id\":67}"));
    }

    [TestCase(null, RpcTransactionErrors.InvalidBlobVersionedHashSize, TestName = "BlobVersionedHash null")]
    [TestCase(new byte[] { 0x01 }, RpcTransactionErrors.InvalidBlobVersionedHashSize, TestName = "BlobVersionedHash too short")]
    [TestCase(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RpcTransactionErrors.InvalidBlobVersionedHashSize, TestName = "BlobVersionedHash too long")]
    [TestCase(new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RpcTransactionErrors.InvalidBlobVersionedHashVersion, TestName = "BlobVersionedHash invalid version 0x00")]
    [TestCase(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RpcTransactionErrors.InvalidBlobVersionedHashVersion, TestName = "BlobVersionedHash invalid version 0x02")]
    public async Task Eth_call_invalid_blob_versioned_hash(byte[]? hash, string expectedError)
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction
            .WithGasLimit(100000)
            .WithMaxFeePerBlobGas(1)
            .WithBlobVersionedHashes([hash!])
            .To(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        BlobTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet))
        {
            GasPrice = null
        };
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32000,\"message\":\"{expectedError}\"}},\"id\":67}}"));
    }

    [Test]
    public async Task Eth_call_bubbles_up_precompile_errors()
    {
        using Context ctx = await Context.Create(new SingleReleaseSpecProvider(Osaka.Instance, BlockchainIds.Mainnet, BlockchainIds.Mainnet));
        Transaction tx = Build.A.Transaction
            .WithData(Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000004010000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000101"))
            .WithTo(new Address("0x0000000000000000000000000000000000000005"))
            .WithGasLimit(1000000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        LegacyTransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32003,\"message\":\"Precompile MODEXP failed with error: one or more of base/exponent/modulus length exceeded 1024 bytes\"},\"id\":67}"));
    }

    [TestCase("""{"input":"0x23e52","gasPrice":"0x1"}""", TestName = "Legacy tx odd-length input")]
    [TestCase("""{"data":"0xABC","gasPrice":"0x1"}""", TestName = "Legacy tx odd-length data")]
    [TestCase("""{"input":"0x1ab"}""", TestName = "EIP1559 tx odd-length input")]
    [TestCase("""{"data":"0x1ab","maxFeePerGas":"0x1"}""", TestName = "EIP1559 tx odd-length data")]
    public async Task Eth_call_odd_length_input_returns_invalid_params(string txJson)
    {
        using Context ctx = await Context.Create();
        JsonElement txParam = JsonDocument.Parse(txJson).RootElement;
        string serialized = await ctx.Test.TestEthRpc("eth_call", txParam, "latest");
        JToken.Parse(serialized)["error"]!["code"]!.Value<int>().Should().Be(-32602);
    }

    [Test]
    public async Task Eth_call_non_existent_block_returns_not_found()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>("{\"from\":\"0xEF04bc7821433f080461BBAE815182E3d7bBb61A\",\"to\":\"0x4debB0dF4da8D1f51EF67B727c3F1c0eCC7ed009\",\"gas\":\"0x5208\"}");
        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "0xFFFFFFFF");

        JToken.Parse(serialized)["error"]!["code"]!.Value<int>().Should().Be(-32000);
        JToken.Parse(serialized)["error"]!["message"]!.Value<string>().Should().Be("header not found");
    }

    private static async Task TestEthCallOutOfGas(Context ctx, long? specifiedGasLimit)
    {
        string gasParam = specifiedGasLimit.HasValue ? $", \"gas\": \"0x{specifiedGasLimit.Value:X}\"" : "";
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\"{gasParam}, \"data\": \"{InfiniteLoopCode.ToHexString(true)}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction);
        JToken.Parse(serialized).Should().BeEquivalentTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32003,\"message\":\"out of gas\"}},\"id\":67}}");
    }

    // Each test uses a state override to inject one-opcode contract code at the target address,
    // then verifies the returned value matches what was supplied in blockOverride.
    [TestCase(
        "NUMBER opcode returns overridden block number",
        """{"to":"0xc200000000000000000000000000000000000000","gas":"0x30D40"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x4360005260206000f3"}}""",
        """{"number":"0x89543F"}""",
        "0x000000000000000000000000000000000000000000000000000000000089543f"
    )]
    [TestCase(
        "TIMESTAMP opcode returns overridden timestamp",
        """{"to":"0xc200000000000000000000000000000000000000","gas":"0x30D40"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x4260005260206000f3"}}""",
        """{"time":"0x68E0F100"}""",
        "0x0000000000000000000000000000000000000000000000000000000068e0f100"
    )]
    [TestCase(
        "GASLIMIT opcode returns overridden gas limit",
        """{"to":"0xc200000000000000000000000000000000000000","gas":"0x30D40"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x4560005260206000f3"}}""",
        """{"gasLimit":"0x7E1200"}""",
        "0x00000000000000000000000000000000000000000000000000000000007e1200"
    )]
    [TestCase(
        "COINBASE opcode returns overridden fee recipient",
        """{"to":"0xc200000000000000000000000000000000000000","gas":"0x30D40"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x4160005260206000f3"}}""",
        """{"feeRecipient":"0x1111111111111111111111111111111111111111"}""",
        "0x0000000000000000000000001111111111111111111111111111111111111111"
    )]
    public async Task Eth_call_with_block_override(string name, string txJson, string stateOverrideJson, string blockOverrideJson, string expectedResult)
    {
        object? transaction = JsonSerializer.Deserialize<object>(txJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);
        object? blockOverride = JsonSerializer.Deserialize<object>(blockOverrideJson);

        using Context ctx = await Context.Create();

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", stateOverride, blockOverride);

        JToken.Parse(serialized)["result"]!.Value<string>().Should().Be(expectedResult);
    }

    [Test]
    public async Task Eth_call_feeless_with_positive_blockOverride_baseFeePerGas_succeeds()
    {
        // Scenario: caller sends no fee fields (fee-less call) but blockOverride.baseFeePerGas > 0.
        using Context ctx = await Context.CreateWithLondonEnabled();

        object? transaction = JsonSerializer.Deserialize<object>(
            $"{{\"from\":\"{SecondaryTestAddress}\",\"to\":\"0xc200000000000000000000000000000000000000\"}}");
        object? blockOverride = JsonSerializer.Deserialize<object>("""{"baseFeePerGas":"0x100"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_call", transaction, "latest", null, blockOverride);

        JToken.Parse(serialized)["error"].Should().BeNull(because:
            "fee-less call must succeed even when blockOverride.baseFeePerGas > 0");
    }

    [Test]
    public async Task Eth_call_blobBaseFeePerGas_override_test()
    {
        ISpecProvider specProvider = new TestSpecProvider(Cancun.Instance);
        ulong? excessBlobGas = 1ul;

        Block[] blocks = [
            Build.A.Block.WithNumber(0).WithExcessBlobGas(excessBlobGas).TestObject,
        ];

        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;

        using TestRpcBlockchain test = await TestRpcBlockchain
            .ForTest(SealEngineType.NethDev)
            .WithBlockFinder(blockTree)
            .Build(specProvider);

        object? stateOverride = JsonSerializer.Deserialize<object>(
        """{"0xc200000000000000000000000000000000000000":{"code":"0x4a60005260206000f3"}}""");

        object? transaction = JsonSerializer.Deserialize<object>(
        """{"to":"0xc200000000000000000000000000000000000000","gas":"0x100000"}""");

        object? blockOverride = JsonSerializer.Deserialize<object>(
        """{"blobBaseFee":"0x02"}""");

        string withOverride = await test.TestEthRpc(
            "eth_call", transaction, "latest", stateOverride, blockOverride);

        JToken parsed = JToken.Parse(withOverride);

        parsed["error"]?.Should().BeNull("opcode must be valid under Cancun");

        string? resultHex = parsed["result"]?.Value<string>();
        resultHex.Should().NotBeNull();

        UInt256 overriddenFee = Bytes.FromHexString(resultHex!).ToUInt256();
        overriddenFee.Should().Be((UInt256)0x02);
    }

}
