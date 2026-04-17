// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Text;
using Nethermind.Abi;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_estimateGas_web3_should_return_insufficient_balance_error()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, TestAccount);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestAccountAddress}\",\"gasPrice\":\"0x100000\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\", \"value\": 500}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"insufficient sender balance\"},\"id\":67}"));
        AssertAccountDoesNotExist(ctx, TestAccount);
    }


    [Test]
    public async Task Eth_estimateGas_web3_sample_not_enough_gas_system_account()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, Address.SystemUser);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}"));
        AssertAccountDoesNotExist(ctx, Address.SystemUser);
    }

    [Test]
    public async Task Eth_estimateGas_web3_sample_not_enough_gas_other_account()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, TestAccount);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestAccountAddress}\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}"));
        AssertAccountDoesNotExist(ctx, TestAccount);
    }

    [Test]
    public async Task Eth_estimateGas_web3_above_block_gas_limit()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, TestAccount);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestAccountAddress}\",\"gas\":\"0x100000\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}"));
        AssertAccountDoesNotExist(ctx, TestAccount);
    }

    [TestCase(false, 2)]
    [TestCase(true, 2)]
    [TestCase(true, 17)]
    public async Task Eth_create_access_list_calculates_proper_gas(bool optimize, long loads)
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, _) = GetTestAccessList(loads);

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
        string serializedCreateAccessList = await test.TestEthRpc("eth_createAccessList",
            transaction, "0x0", null, optimize.ToString().ToLower());

        transaction.AccessList = test.JsonSerializer.Deserialize<AccessListForRpc>(JToken.Parse(serializedCreateAccessList).SelectToken("result.accessList")!.ToString());
        string serializedEstimateGas =
            await test.TestEthRpc("eth_estimateGas", transaction, "0x0");

        string? gasUsedEstimateGas = JToken.Parse(serializedEstimateGas).Value<string>("result");
        string? gasUsedCreateAccessList =
            JToken.Parse(serializedCreateAccessList).SelectToken("result.gasUsed")?.Value<string>();

        long gasUsedAccessList = (long)Bytes.FromHexString(gasUsedCreateAccessList!).ToUInt256();
        long gasUsedEstimate = (long)Bytes.FromHexString(gasUsedEstimateGas!).ToUInt256();
        Assert.That(gasUsedEstimate, Is.EqualTo((double)gasUsedAccessList).Within(1.5).Percent);
    }

    [TestCase(true, 0xeee7, 0xf71b)]
    [TestCase(false, 0xeee7, 0xee83)]
    public async Task Eth_estimate_gas_with_accessList(bool senderAccessList, long gasPriceWithoutAccessList,
        long gasPriceWithAccessList)
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithConfig(new JsonRpcConfig() { EstimateErrorMargin = 0 })
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListForRpc accessList) = GetTestAccessList(2, senderAccessList);

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"from\": \"{Address.SystemUser}\", \"data\": \"{code.ToHexString(true)}\"}}");
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
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
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
            $"{{\"from\": \"{BatTokenAddress}\", \"to\": \"{BatTokenAddress}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x10\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing_after_1559_legacy()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x100000000\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing_after_1559_new_type_of_transaction()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"to\": \"{SecondaryTestAddress}\", \"type\": \"0x2\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_base_fee_opcode()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string dataStr = BaseFeeReturnCode.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"type\": \"0x2\", \"data\": \"{dataStr}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xe891\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_revert()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string errorMessage = "wrong-calldatasize";
        string hexEncodedErrorMessage = Encoding.UTF8.GetBytes(errorMessage).ToHexString(true);

        byte[] code = Prepare.EvmCode
            .RevertWithError(errorMessage)
            .Done;

        string dataStr = code.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        // Raw bytes are not ABI-encoded Error(string), so message stays plain "execution reverted"
        // and the raw bytes appear only in data (matching Geth behaviour).
        Assert.That(
            serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"{{hexEncodedErrorMessage}}"},"id":67}"""));
    }

    [Test]
    public async Task Estimate_gas_with_custom_error_returns_hex_selector()
    {
        // A no-parameter custom error (e.g. ActionFailed()) produces exactly 4 revert bytes.
        // message must be plain "execution reverted" (matching Geth); raw bytes go only in data.
        using Context ctx = await Context.CreateWithLondonEnabled();

        // keccak4("ActionFailed()") = 0x080a1c27
        byte[] selector = [0x08, 0x0a, 0x1c, 0x27];

        byte[] code = Prepare.EvmCode
            .RevertWithCustomError(selector)
            .Done;

        string dataStr = code.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""");
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"0x080a1c27"},"id":67}"""));
    }

    [Test]
    public async Task Estimate_gas_with_abi_encoded_revert()
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
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted: {{errorMessage}}","data":"{{abiEncodedErrorMessage}}"},"id":67}"""));
    }

    [Test]
    public async Task should_estimate_transaction_with_deployed_code_when_eip3607_enabled()
    {
        OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1, IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(releaseSpec) { AllowTestChainOverride = false };
        using Context ctx = await Context.Create(specProvider, configurer: builder => builder
            .WithGenesisPostProcessor((block, worldState) =>
            {
                worldState.InsertCode(TestItem.AddressA, "H"u8.ToArray(), London.Instance);
            }));

        Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        LegacyTransactionForRpc transaction = new(
            tx,
            new(tx.ChainId ?? BlockchainIds.Mainnet))
        {
            To = TestItem.AddressB,
            GasPrice = 0
        };

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
        "Executes precompile using overridden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        """{"jsonrpc":"2.0","result":"0x6440","id":67}""" // ECRecover call + intrinsic transaction cost
    )]
    public async Task Estimate_gas_with_state_override(string name, string transactionJson, string stateOverrideJson, string expectedResult)
    {
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        TestSpecProvider specProvider = new(Prague.Instance);
        using Context ctx = await Context.Create(specProvider);

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        JToken.Parse(serialized).Should().BeEquivalentTo(expectedResult);
    }

    [TestCase(
        "When balance and nonce is overridden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","value":"0x123"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x123", "nonce": "0x123"}}"""
    )]
    [TestCase(
        "When address code is overridden",
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
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        string resultOverrideBefore = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        string resultNoOverride = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest");

        string resultOverrideAfter = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

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

        await TestEstimateGasOutOfGas(ctx, null, blockGasLimit, $"gas required exceeds allowance ({blockGasLimit})");
    }

    [Test]
    public async Task Estimate_gas_not_limited_by_latest_block_gas_used()
    {
        using Context ctx = await Context.Create();

        Block head = ctx.Test.BlockTree.FindHeadBlock()!;
        head.Header.GasUsed = head.Header.GasLimit - 10_000;

        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{BatTokenAddress}\", \"to\": \"{SecondaryTestAddress}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_uses_specified_gas_limit()
    {
        using Context ctx = await Context.Create();
        await TestEstimateGasOutOfGas(ctx, 30000000, 30000000, $"gas required exceeds allowance ({30000000})");
    }

    [Test]
    public async Task Estimate_gas_cannot_exceed_gas_cap()
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.GasCap = 50000000;
        await TestEstimateGasOutOfGas(ctx, 300000000, 50000000, $"gas required exceeds allowance ({50000000})");
    }

    [Test]
    public async Task Estimate_gas_returns_allowance_error_when_balance_insufficient_for_gas_price()
    {
        // Geth parity: when sender balance is too low to cover gas at the given gasPrice,
        // cap rightBound to allowance = balance / gasPrice, execute at that cap, fail OOG,
        // and return "gas required exceeds allowance (N)".
        using Context ctx = await Context.CreateWithLondonEnabled();

        object transaction = JsonSerializer.Deserialize<object>(
            """{"from":"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gasPrice":"0xBA43B7400"}""")!;
        object stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x100000000000"}}""")!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        JToken.Parse(serialized).Should().BeEquivalentTo(
            """{"jsonrpc":"2.0","error":{"code":-32000,"message":"gas required exceeds allowance (351)"},"id":67}""");
    }

    [Test]
    public async Task Eth_estimateGas_ignores_invalid_nonce()
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

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x520c\",\"id\":67}"));

    }

    [Test]
    public async Task Eth_estimateGas_simple_transfer()
    {
        using Context ctx = await Context.Create();
        byte[] code = [];
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100000)
            .WithData(code)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));

        transaction.GasPrice = null;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_estimateGas_succeeds_when_gas_price_set_but_balance_below_block_gas_limit_times_gas_price()
    {
        // Regression for: balance < blockGasLimit × gasPrice but balance is enough for actual gas cost.
        // Before fix: TransactionProcessor rejected with "insufficient MaxFeePerGas for sender balance"
        // because the EIP-1559 pre-check used tx.GasLimit (= blockGasLimit) instead of the actual estimated gas.
        // blockGasLimit(4M) × gasPrice(50Gwei) = 0.2 ETH > balance(0.1 ETH).
        // Actual gas needed ≈ 0x53b8 ≈ 21432 → cost = 21432 × 50Gwei ≪ 0.1 ETH.
        using Context ctx = await Context.CreateWithLondonEnabled();

        object transaction = JsonSerializer.Deserialize<object>("""{"from":"0x244A93D0f7F27b845060abEd22B23aD914C71B54","to":"0xcd4EC7b66fbc029C116BA9Ffb3e59351c20B5B06","gasPrice":"0x1D91CA3600" , "gas":"0xE234" , "data": "0xca120b1f000000000000000000000000244a93d0f7f27b845060abed22b23ad914c71b54eaafa8798f3f2c657b5f86f543682ec694db0344b923fe7a508c522d22938945000000000000000000000000c02aaa39b223fe8d0a0e5c4f27ead9083c756cc20000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000000000000000000000087df2103941187dc230eab1a148a587cb8a03afb00000000000000000000000000000000000000000000000000000000000000030000000000000000000000000000000000000000000000000000000000000002"}""", JsonSerializerOptions.Default)!;
        // balance = 0.002 ETH (below gas × gasPrice = 0.0029 ETH but above intrinsicGas × gasPrice)
        // target address has minimal always-revert bytecode: PUSH1 0, PUSH1 0, REVERT
        object stateOverride = JsonSerializer.Deserialize<object>("""{"0x244A93D0f7F27b845060abEd22B23aD914C71B54":{"balance":"0x120e3efb0a71c6"},"0xcd4EC7b66fbc029C116BA9Ffb3e59351c20B5B06":{"code":"0x6080604052600436106100fe5760003560e01c8063715018a6116100955780639cec6392116100645780639cec63921461026a578063c19d93fb1461027d578063ca120b1f14610292578063f2fde38b146102b2578063fee03e9e146102d2576100fe565b8063715018a6146102095780638da5cb5b1461021e5780638f32d59b146102335780639704dc4414610255576100fe565b806338af3eed116100d157806338af3eed1461019d5780634df97bc5146101bf57806355d5d326146101d45780636e667db3146101f4576100fe565b806302329e1014610103578063049944b61461012e5780631b4c98741461015b5780631c31f7101461017b575b600080fd5b34801561010f57600080fd5b506101186102e7565b60405161012591906123a2565b60405180910390f35b34801561013a57600080fd5b5061014e610149366004611dc6565b6102f6565b6040516101259190612436565b34801561016757600080fd5b5061014e610176366004611de5565b61032f565b34801561018757600080fd5b5061019b610196366004611c11565b610350565b005b3480156101a957600080fd5b506101b261039f565b6040516101259190612328565b3480156101cb57600080fd5b506101b26103ae565b3480156101e057600080fd5b5061019b6101ef366004611c11565b6103bd565b34801561020057600080fd5b50610118610403565b34801561021557600080fd5b5061019b610412565b34801561022a57600080fd5b506101b2610480565b34801561023f57600080fd5b5061024861048f565b604051610125919061236b565b34801561026157600080fd5b506101186104b3565b61019b610278366004611d39565b6104c2565b34801561028957600080fd5b506101186106cd565b34801561029e57600080fd5b5061019b6102ad366004611cfb565b6106dc565b3480156102be57600080fd5b5061019b6102cd366004611c11565b6107ea565b3480156102de57600080fd5b5061011861081a565b6004546001600160a01b031681565b60606103278260405160200161030c9190612542565b60405160208183030381529060405280519060200120610829565b90505b919050565b6060610347838360405160200161030c929190612551565b90505b92915050565b61035861048f565b61037d5760405162461bcd60e51b8152600401610374906124b7565b60405180910390fd5b600180546001600160a01b0319166001600160a01b0392909216919091179055565b6001546001600160a01b031681565b6002546001600160a01b031681565b6103c561048f565b6103e15760405162461bcd60e51b8152600401610374906124b7565b600280546001600160a01b0319166001600160a01b0392909216919091179055565b6003546001600160a01b031681565b61041a61048f565b6104365760405162461bcd60e51b8152600401610374906124b7565b600080546040516001600160a01b03909116907f8be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e0908390a3600080546001600160a01b0319169055565b6000546001600160a01b031690565b600080546001600160a01b03166104a461093c565b6001600160a01b031614905090565b6007546001600160a01b031681565b6104e86104d436889003880188611dc6565b6104e336889003880188611e21565b610940565b61050f6104fa36889003880188611dc6565b8561050a36879003870187611e21565b610a6d565b600061053b61010088013561052f6101208a01358663ffffffff610ac316565b9063ffffffff610afd16565b905061055b61054f36899003890189611d1a565b88610100013585610b3f565b600061056d60a0890160808a01611cbf565b600481111561057857fe5b14156105965760405162461bcd60e51b8152600401610374906124c7565b60006105a9610100890160e08a01611cbf565b60048111156105b457fe5b14156105c4576105c48186610c61565b60006105ef6105d960a08a0160808b01611cbf565b6105ea6101008b0160e08c01611cbf565b610c96565b90506001600160a01b038316610603573392505b610659610618368a90038a0160408b01611cdd565b8561062660208c018c611c11565b86600186600281111561063557fe5b148b8e61014001358f60000160a0018036036106549190810190611cdd565b610d25565b6106aa61066e368a90038a0160a08b01611cdd565b833361067d60208d018d611c11565b600286600281111561068b57fe5b148d61014001358c8f6000016040018036036106549190810190611cdd565b6106c36106bc368a90038a018a611dc6565b8585610d4a565b5050505050505050565b6006546001600160a01b031681565b336106ea6020830183611c11565b6001600160a01b0316146107105760405162461bcd60e51b8152600401610374906124d7565b600654604051631d03b74d60e11b81526001600160a01b0390911690633a076e9a9061074490849060001990600401612507565b600060405180830381600087803b15801561075e57600080fd5b505af1158015610772573d6000803e3d6000fd5b505050606082018035915061078a9060408401611c11565b6001600160a01b03167fbfe0e802e586c99960de1a111c80f598b281996d65080d74dbe29986f55b274a336107c560c0860160a08701611c11565b6040516107df92919060c088013590602089013590612336565b60405180910390a350565b6107f261048f565b61080e5760405162461bcd60e51b8152600401610374906124b7565b61081781610dc9565b50565b6005546001600160a01b031681565b604080518082018252601081526f181899199a1a9b1b9c1cb0b131b232b360811b60208201528151828152606081810184529283919060208201818038833901905050905060005b60208110156109345782600486836020811061088957fe5b1a60f81b6001600160f81b031916901c60f81c60ff16815181106108a957fe5b602001015160f81c60f81b8282600202815181106108c357fe5b60200101906001600160f81b031916908160001a905350828582602081106108e757fe5b825191901a600f169081106108f857fe5b602001015160f81c60f81b82826002026001018151811061091557fe5b60200101906001600160f81b031916908160001a905350600101610871565b509392505050565b3390565b805160ff1615801561095457506020810151155b801561096257506040810151155b15610a0857600754604051632aee7a3160e01b81526001600160a01b0390911690632aee7a3190610997908590600401612542565b60206040518083038186803b1580156109af57600080fd5b505afa1580156109c3573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052506109e79190810190611ca1565b610a035760405162461bcd60e51b815260040161037490612487565b610a69565b8160000151600001516001600160a01b0316610a43826000015183602001518460400151610a35876102f6565b92919063ffffffff610e4a16565b6001600160a01b031614610a695760405162461bcd60e51b815260040161037490612487565b5050565b6002548151602083015160408401516001600160a01b0390931692610a98929190610a35888861032f565b6001600160a01b031614610abe5760405162461bcd60e51b815260040161037490612497565b505050565b600082610ad25750600061034a565b82820282848281610adf57fe5b04146103475760405162461bcd60e51b8152600401610374906124a7565b600061034783836040518060400160405280601a81526020017f536166654d6174683a206469766973696f6e206279207a65726f000000000000815250610f27565b6006546040516303ec000360e61b81526000916001600160a01b03169063fb0000c090610b70908790600401612524565b60206040518083038186803b158015610b8857600080fd5b505afa158015610b9c573d6000803e3d6000fd5b505050506040513d601f19601f82011682018060405250610bc09190810190611e3f565b90506000610bd4828463ffffffff610f6016565b905083811115610bf65760405162461bcd60e51b815260040161037490612447565b600654604051631d03b74d60e11b81526001600160a01b0390911690633a076e9a90610c289088908590600401612533565b600060405180830381600087803b158015610c4257600080fd5b505af1158015610c56573d6000803e3d6000fd5b505050505050505050565b6000610c73838363ffffffff610f8516565b90508083013414610abe5760405162461bcd60e51b8152600401610374906124f7565b60006003836004811115610ca657fe5b1480610cbd57506004836004811115610cbb57fe5b145b8015610ce957506003826004811115610cd257fe5b1480610ce957506004826004811115610ce757fe5b145b15610cf65750600061034a565b816004811115610d0257fe5b836004811115610d0e57fe5b1115610d1c5750600261034a565b50600192915050565b83610d3b57610d3688888888610f9d565b6106c3565b6106c3888888888787876111f0565b825160408082015160208082015191518188015185516060870151805190850151878c01519590980151965195976001600160a01b03909416967fdddcdb07e460849cf04a4445b7af9faf01b7f5c7ba75deaf969ac5ed830312c396610dbc9694959394929392918b918d919061257c565b60405180910390a3505050565b6001600160a01b038116610def5760405162461bcd60e51b815260040161037490612467565b600080546040516001600160a01b03808516939216917f8be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e091a3600080546001600160a01b0319166001600160a01b0392909216919091179055565b600060608590506060610ec36040518060400160405280601a81526020017f19457468657265756d205369676e6564204d6573736167653a0a000000000000815250610e9684516114f5565b604080516000808252602082018181528284018281526060840192835260808401909452889390916115b6565b90506001818051906020012087878760405160008152602001604052604051610eef9493929190612379565b6020604051602081039080840390855afa158015610f11573d6000803e3d6000fd5b5050604051601f19015198975050505050505050565b60008183610f485760405162461bcd60e51b81526004016103749190612436565b506000838581610f5457fe5b049150505b9392505050565b6000828201838110156103475760405162461bcd60e51b815260040161037490612477565b600061034761271061052f858563ffffffff610ac316565b600084604001516004811115610faf57fe5b1415610ff45760405181906001600160a01b0382169085156108fc029086906000818181858888f19350505050158015610fed573d6000803e3d6000fd5b50506111ea565b60018460400151600481111561100657fe5b141561109b5760208401511561102e5760405162461bcd60e51b8152600401610374906124e7565b600554845160405163776062c360e01b81526001600160a01b039092169163776062c3916110649186908690899060040161240e565b600060405180830381600087803b15801561107e57600080fd5b505af1158015611092573d6000803e3d6000fd5b505050506111ea565b6003846040015160048111156110ad57fe5b141561110e57826001146110d35760405162461bcd60e51b815260040161037490612457565b60035484516020860151604051637b84dc8360e11b81526001600160a01b039093169263f709b906926110649290918791879160040161240e565b60048460400151600481111561112057fe5b141561117f57826001146111465760405162461bcd60e51b815260040161037490612457565b60048054855160208701516040516321143af960e21b81526001600160a01b0390931693638450ebe4936110649392889288920161240e565b60035484516020860151604051639c1c2ee960e01b81526001600160a01b0390931692639c1c2ee9926111bc929091879187918a906004016123b0565b600060405180830381600087803b1580156111d657600080fd5b505af11580156106c3573d6000803e3d6000fd5b50505050565b60006111ff888789878761185c565b905060028260400151600481111561121357fe5b1480156112a1575081516040516301ffc9a760e01b81526001600160a01b03909116906301ffc9a79061125190632dde656160e21b90600401612394565b60206040518083038186803b15801561126957600080fd5b505afa15801561127d573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052506112a19190810190611ca1565b8061136157506003826040015160048111156112b957fe5b14806112d457506004826040015160048111156112d257fe5b145b8015611361575081516040516301ffc9a760e01b81526001600160a01b03909116906301ffc9a79061131190632dde656160e21b90600401612394565b60206040518083038186803b15801561132957600080fd5b505afa15801561133d573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052506113619190810190611ca1565b156114e8578151602083015160405163b9c4d9fb60e01b81526060916001600160a01b0384169163b9c4d9fb9161139a9160040161256e565b60006040518083038186803b1580156113b257600080fd5b505afa1580156113c6573d6000803e3d6000fd5b505050506040513d6000823e601f3d908101601f191682016040526113ee9190810190611c37565b90506060826001600160a01b0316630ebd4c7f86602001516040518263ffffffff1660e01b8152600401611422919061256e565b60006040518083038186803b15801561143a57600080fd5b505afa15801561144e573d6000803e3d6000fd5b505050506040513d6000823e601f3d908101601f191682016040526114769190810190611c6c565b9050815181511461148657600080fd5b60005b81518110156114e3576000806114b3878e8686815181106114a657fe5b60200260200101516118ca565b915091508196506114d98e828e8887815181106114cc57fe5b6020026020010151610f9d565b5050600101611489565b505050505b84610c5689838984610f9d565b60608161151a57506040805180820190915260018152600360fc1b602082015261032a565b8160005b811561153257600101600a8204915061151e565b6060816040519080825280601f01601f19166020018201604052801561155f576020820181803883390190505b50905060001982015b85156115ad57600a860660300160f81b8282806001900393508151811061158b57fe5b60200101906001600160f81b031916908160001a905350600a86049550611568565b50949350505050565b60608082518451865188518a518c518e510101010101016040519080825280601f01601f1916602001820160405280156115f7576020820181803883390190505b5090506000805b8a5181101561164f578a818151811061161357fe5b602001015160f81c60f81b83838060010194508151811061163057fe5b60200101906001600160f81b031916908160001a9053506001016115fe565b5060005b89518110156116a45789818151811061166857fe5b602001015160f81c60f81b83838060010194508151811061168557fe5b60200101906001600160f81b031916908160001a905350600101611653565b5060005b88518110156116f9578881815181106116bd57fe5b602001015160f81c60f81b8383806001019450815181106116da57fe5b60200101906001600160f81b031916908160001a9053506001016116a8565b5060005b875181101561174e5787818151811061171257fe5b602001015160f81c60f81b83838060010194508151811061172f57fe5b60200101906001600160f81b031916908160001a9053506001016116fd565b5060005b86518110156117a35786818151811061176757fe5b602001015160f81c60f81b83838060010194508151811061178457fe5b60200101906001600160f81b031916908160001a905350600101611752565b5060005b85518110156117f8578581815181106117bc57fe5b602001015160f81c60f81b8383806001019450815181106117d957fe5b60200101906001600160f81b031916908160001a9053506001016117a7565b5060005b845181101561184d5784818151811061181157fe5b602001015160f81c60f81b83838060010194508151811061182e57fe5b60200101906001600160f81b031916908160001a9053506001016117fc565b50909998505050505050505050565b600080600061186c8687876118ca565b90925090506000611883878663ffffffff610f8516565b90506000611897828463ffffffff610f6016565b905080156118ba576001546118ba908b9083908c906001600160a01b0316610f9d565b5091925050505b95945050505050565b6000806118e6856118e1868663ffffffff610f8516565b6118f2565b91509150935093915050565b600080828411156119085750508082038161190f565b5060009050825b9250929050565b803561034a81612712565b805161034a81612712565b600082601f83011261193d57600080fd5b815161195061194b8261261a565b6125f3565b9150818183526020840193506020810190508385602084028201111561197557600080fd5b60005b838110156119a1578161198b8882611921565b8452506020928301929190910190600101611978565b5050505092915050565b600082601f8301126119bc57600080fd5b81516119ca61194b8261261a565b915081818352602084019350602081019050838560208402820111156119ef57600080fd5b60005b838110156119a15781611a058882611bfb565b84525060209283019291909101906001016119f2565b805161034a81612726565b803561034a8161272f565b803561034a81612738565b600060608284031215611a4e57600080fd5b611a5860606125f3565b90506000611a668484611916565b8252506020611a7784848301611a26565b6020830152506040611a8b84828501611a31565b60408301525092915050565b60006101008284031215611aaa57600080fd5b50919050565b60006101008284031215611ac357600080fd5b611acd60806125f3565b90506000611adb8484611916565b8252506020611aec84848301611a26565b6020830152506040611b0084828501611a3c565b60408301525060a0611b1484828501611a3c565b60608301525092915050565b60006101608284031215611aaa57600080fd5b60006101608284031215611b4657600080fd5b611b5060806125f3565b90506000611b5e8484611ab0565b825250610100611b7084848301611a26565b602083015250610120611b8584828501611a26565b604083015250610140611b1484828501611a26565b600060608284031215611aaa57600080fd5b600060608284031215611bbe57600080fd5b611bc860606125f3565b90506000611bd68484611c06565b8252506020611be784848301611a26565b6020830152506040611a8b84828501611a26565b805161034a8161272f565b803561034a81612745565b600060208284031215611c2357600080fd5b6000611c2f8484611916565b949350505050565b600060208284031215611c4957600080fd5b815167ffffffffffffffff811115611c6057600080fd5b611c2f8482850161192c565b600060208284031215611c7e57600080fd5b815167ffffffffffffffff811115611c9557600080fd5b611c2f848285016119ab565b600060208284031215611cb357600080fd5b6000611c2f8484611a1b565b600060208284031215611cd157600080fd5b6000611c2f8484611a31565b600060608284031215611cef57600080fd5b6000611c2f8484611a3c565b60006101008284031215611d0e57600080fd5b6000611c2f8484611a97565b60006101008284031215611d2d57600080fd5b6000611c2f8484611ab0565b6000806000806000806102808789031215611d5357600080fd5b6000611d5f8989611b20565b965050610160611d7189828a01611b9a565b9550506101c0611d8389828a01611a26565b9450506101e0611d9589828a01611b9a565b935050610240611da789828a01611a26565b925050610260611db989828a01611916565b9150509295509295509295565b60006101608284031215611dd957600080fd5b6000611c2f8484611b33565b6000806101808385031215611df957600080fd5b6000611e058585611b33565b925050610160611e1785828601611a26565b9150509250929050565b600060608284031215611e3357600080fd5b6000611c2f8484611bac565b600060208284031215611e5157600080fd5b6000611c2f8484611bfb565b611e66816126b5565b82525050565b611e6681612679565b611e6681612684565b611e6681612689565b611e668161268c565b611e66816126bc565b611e66816126c7565b6000611ead8261263b565b611eb7818561263f565b9350611ec78185602086016126d2565b611ed0816126fe565b9093019392505050565b6000611ee760248361263f565b7f6e6f7420656e6f7567682073746f636b206f66206f7264657220666f7220627581526379696e6760e01b602082015260400192915050565b6000611f2d601d8361263f565b7f76616c75652073686f756c64206265203120666f72204552432d373231000000815260200192915050565b6000611f6660268361263f565b7f4f776e61626c653a206e6577206f776e657220697320746865207a65726f206181526564647265737360d01b602082015260400192915050565b6000611fae601b8361263f565b7f536166654d6174683a206164646974696f6e206f766572666c6f770000000000815260200192915050565b6000611fe760138361263f565b72696e636f7272656374207369676e617475726560681b815260200192915050565b6000612016601d8361263f565b7f696e636f727265637420627579657220666565207369676e6174757265000000815260200192915050565b600061204f60218361263f565b7f536166654d6174683a206d756c7469706c69636174696f6e206f766572666c6f8152607760f81b602082015260400192915050565b600061209260208361263f565b7f4f776e61626c653a2063616c6c6572206973206e6f7420746865206f776e6572815260200192915050565b60006120cb60218361263f565b7f455448206973206e6f7420737570706f72746564206f6e2073656c6c207369648152606560f81b602082015260400192915050565b600061034a60008361263f565b600061211b600c8361263f565b6b3737ba1030b71037bbb732b960a11b815260200192915050565b600061214360138361263f565b720746f6b656e49642073686f756c64206265203606c1b815260200192915050565b600061217260168361263f565b751b5cd9cb9d985b1d59481a5cc81a5b98dbdc9c9958dd60521b815260200192915050565b606082016121a58280612648565b6121af8482611e6c565b506121bd602083018361266a565b6121ca6020850182611e7e565b506121d86040830183612657565b6111ea6040850182611e99565b805160608301906121f68482611e6c565b5060208201516122096020850182611e7e565b5060408201516111ea6040850182611e99565b610100820161222b8280612648565b6122358482611e6c565b50612243602083018361266a565b6122506020850182611e7e565b5061225e6040830183612666565b61226b6040850182612197565b5061227960a0830183612666565b6111ea60a0850182612197565b80516101008301906122988482611e6c565b5060208201516122ab6020850182611e7e565b5060408201516122be60408501826121e5565b5060608201516111ea60a08501826121e5565b80516101608301906122e38482612286565b5060208201516122f7610100850182611e7e565b50604082015161230b610120850182611e7e565b5060608201516111ea610140850182611e7e565b611e66816126af565b6020810161034a8284611e6c565b608081016123448287611e5d565b6123516020830186611e6c565b61235e6040830185611e7e565b6118c16060830184611e7e565b6020810161034a8284611e75565b608081016123878287611e7e565b612351602083018661231f565b6020810161034a8284611e87565b6020810161034a8284611e90565b60c081016123be8288611e90565b6123cb6020830187611e6c565b6123d86040830186611e6c565b6123e56060830185611e7e565b6123f26080830184611e7e565b81810360a083015261240381612101565b979650505050505050565b6080810161241c8287611e90565b6124296020830186611e6c565b61235e6040830185611e6c565b602080825281016103478184611ea2565b6020808252810161032781611eda565b6020808252810161032781611f20565b6020808252810161032781611f59565b6020808252810161032781611fa1565b6020808252810161032781611fda565b6020808252810161032781612009565b6020808252810161032781612042565b6020808252810161032781612085565b60208082528101610327816120be565b602080825281016103278161210e565b6020808252810161032781612136565b6020808252810161032781612165565b6101208101612516828561221c565b610f59610100830184611e7e565b610100810161034a8284612286565b61012081016125168285612286565b610160810161034a82846122d1565b610180810161256082856122d1565b610f59610160830184611e7e565b6020810161034a8284611e7e565b610100810161258b828b611e7e565b612598602083018a611e6c565b6125a56040830189611e6c565b6125b26060830188611e7e565b6125bf6080830187611e7e565b6125cc60a0830186611e6c565b6125d960c0830185611e7e565b6125e660e0830184611e7e565b9998505050505050505050565b60405181810167ffffffffffffffff8111828210171561261257600080fd5b604052919050565b600067ffffffffffffffff82111561263157600080fd5b5060209081020190565b5190565b90815260200190565b60006103476020840184611916565b60006103476020840184611a31565b5090565b60006103476020840184611a26565b6000610327826126a3565b151590565b90565b6001600160e01b03191690565b8061032a81612708565b6001600160a01b031690565b60ff1690565b6000610327825b600061032782612679565b600061032782612699565b60005b838110156126ed5781810151838201526020016126d5565b838111156111ea5750506000910152565b601f01601f191690565b6005811061081757fe5b61271b81612679565b811461081757600080fd5b61271b81612684565b61271b81612689565b6005811061081757600080fd5b61271b816126af56fea365627a7a72315820559aa28e28a92b415b945f053776f237c6d6a776c69f445be4800b11f7d401db6c6578706572696d656e74616cf564736f6c63430005110040"}}""", JsonSerializerOptions.Default)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        JToken.Parse(serialized).Should().BeEquivalentTo("""{"jsonrpc":"2.0","result":"0x53b8","id":67}""");
    }

    [Test]
    public async Task Eth_estimateGas_returns_execution_reverted_when_gas_price_set_and_contract_reverts()
    {
        // Regression for: balance < explicit_gas × gasPrice, but the EVM should still run and surface the revert.
        // Before fix: TransactionProcessor rejected with "insufficient MaxFeePerGas for sender balance" before EVM ran.
        // explicit_gas(0xE234=57908) × gasPrice(50Gwei) ≈ 0.0029 ETH > balance(0.002 ETH).
        // The target contract reverts unconditionally; after the fix estimation returns "execution reverted".
        using Context ctx = await Context.CreateWithLondonEnabled();
        
        object transaction = JsonSerializer.Deserialize<object>("""{"from":"0x244A93D0f7F27b845060abEd22B23aD914C71B54","to":"0xcd4EC7b66fbc029C116BA9Ffb3e59351c20B5B06","gasPrice":"0xBA43B7400"}""", JsonSerializerOptions.Default)!;
        // balance = 0.002 ETH (below gas × gasPrice = 0.0029 ETH but above intrinsicGas × gasPrice)
        // target address has minimal always-revert bytecode: PUSH1 0, PUSH1 0, REVERT
        object stateOverride = JsonSerializer.Deserialize<object>("""{"0x244A93D0f7F27b845060abEd22B23aD914C71B54":{"balance":"0x120e3efb0a71c6"},"0xcd4EC7b66fbc029C116BA9Ffb3e59351c20B5B06":{"code":"0x6080604052600436106100fe5760003560e01c8063715018a6116100955780639cec6392116100645780639cec63921461026a578063c19d93fb1461027d578063ca120b1f14610292578063f2fde38b146102b2578063fee03e9e146102d2576100fe565b8063715018a6146102095780638da5cb5b1461021e5780638f32d59b146102335780639704dc4414610255576100fe565b806338af3eed116100d157806338af3eed1461019d5780634df97bc5146101bf57806355d5d326146101d45780636e667db3146101f4576100fe565b806302329e1014610103578063049944b61461012e5780631b4c98741461015b5780631c31f7101461017b575b600080fd5b34801561010f57600080fd5b506101186102e7565b60405161012591906123a2565b60405180910390f35b34801561013a57600080fd5b5061014e610149366004611dc6565b6102f6565b6040516101259190612436565b34801561016757600080fd5b5061014e610176366004611de5565b61032f565b34801561018757600080fd5b5061019b610196366004611c11565b610350565b005b3480156101a957600080fd5b506101b261039f565b6040516101259190612328565b3480156101cb57600080fd5b506101b26103ae565b3480156101e057600080fd5b5061019b6101ef366004611c11565b6103bd565b34801561020057600080fd5b50610118610403565b34801561021557600080fd5b5061019b610412565b34801561022a57600080fd5b506101b2610480565b34801561023f57600080fd5b5061024861048f565b604051610125919061236b565b34801561026157600080fd5b506101186104b3565b61019b610278366004611d39565b6104c2565b34801561028957600080fd5b506101186106cd565b34801561029e57600080fd5b5061019b6102ad366004611cfb565b6106dc565b3480156102be57600080fd5b5061019b6102cd366004611c11565b6107ea565b3480156102de57600080fd5b5061011861081a565b6004546001600160a01b031681565b60606103278260405160200161030c9190612542565b60405160208183030381529060405280519060200120610829565b90505b919050565b6060610347838360405160200161030c929190612551565b90505b92915050565b61035861048f565b61037d5760405162461bcd60e51b8152600401610374906124b7565b60405180910390fd5b600180546001600160a01b0319166001600160a01b0392909216919091179055565b6001546001600160a01b031681565b6002546001600160a01b031681565b6103c561048f565b6103e15760405162461bcd60e51b8152600401610374906124b7565b600280546001600160a01b0319166001600160a01b0392909216919091179055565b6003546001600160a01b031681565b61041a61048f565b6104365760405162461bcd60e51b8152600401610374906124b7565b600080546040516001600160a01b03909116907f8be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e0908390a3600080546001600160a01b0319169055565b6000546001600160a01b031690565b600080546001600160a01b03166104a461093c565b6001600160a01b031614905090565b6007546001600160a01b031681565b6104e86104d436889003880188611dc6565b6104e336889003880188611e21565b610940565b61050f6104fa36889003880188611dc6565b8561050a36879003870187611e21565b610a6d565b600061053b61010088013561052f6101208a01358663ffffffff610ac316565b9063ffffffff610afd16565b905061055b61054f36899003890189611d1a565b88610100013585610b3f565b600061056d60a0890160808a01611cbf565b600481111561057857fe5b14156105965760405162461bcd60e51b8152600401610374906124c7565b60006105a9610100890160e08a01611cbf565b60048111156105b457fe5b14156105c4576105c48186610c61565b60006105ef6105d960a08a0160808b01611cbf565b6105ea6101008b0160e08c01611cbf565b610c96565b90506001600160a01b038316610603573392505b610659610618368a90038a0160408b01611cdd565b8561062660208c018c611c11565b86600186600281111561063557fe5b148b8e61014001358f60000160a0018036036106549190810190611cdd565b610d25565b6106aa61066e368a90038a0160a08b01611cdd565b833361067d60208d018d611c11565b600286600281111561068b57fe5b148d61014001358c8f6000016040018036036106549190810190611cdd565b6106c36106bc368a90038a018a611dc6565b8585610d4a565b5050505050505050565b6006546001600160a01b031681565b336106ea6020830183611c11565b6001600160a01b0316146107105760405162461bcd60e51b8152600401610374906124d7565b600654604051631d03b74d60e11b81526001600160a01b0390911690633a076e9a9061074490849060001990600401612507565b600060405180830381600087803b15801561075e57600080fd5b505af1158015610772573d6000803e3d6000fd5b505050606082018035915061078a9060408401611c11565b6001600160a01b03167fbfe0e802e586c99960de1a111c80f598b281996d65080d74dbe29986f55b274a336107c560c0860160a08701611c11565b6040516107df92919060c088013590602089013590612336565b60405180910390a350565b6107f261048f565b61080e5760405162461bcd60e51b8152600401610374906124b7565b61081781610dc9565b50565b6005546001600160a01b031681565b604080518082018252601081526f181899199a1a9b1b9c1cb0b131b232b360811b60208201528151828152606081810184529283919060208201818038833901905050905060005b60208110156109345782600486836020811061088957fe5b1a60f81b6001600160f81b031916901c60f81c60ff16815181106108a957fe5b602001015160f81c60f81b8282600202815181106108c357fe5b60200101906001600160f81b031916908160001a905350828582602081106108e757fe5b825191901a600f169081106108f857fe5b602001015160f81c60f81b82826002026001018151811061091557fe5b60200101906001600160f81b031916908160001a905350600101610871565b509392505050565b3390565b805160ff1615801561095457506020810151155b801561096257506040810151155b15610a0857600754604051632aee7a3160e01b81526001600160a01b0390911690632aee7a3190610997908590600401612542565b60206040518083038186803b1580156109af57600080fd5b505afa1580156109c3573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052506109e79190810190611ca1565b610a035760405162461bcd60e51b815260040161037490612487565b610a69565b8160000151600001516001600160a01b0316610a43826000015183602001518460400151610a35876102f6565b92919063ffffffff610e4a16565b6001600160a01b031614610a695760405162461bcd60e51b815260040161037490612487565b5050565b6002548151602083015160408401516001600160a01b0390931692610a98929190610a35888861032f565b6001600160a01b031614610abe5760405162461bcd60e51b815260040161037490612497565b505050565b600082610ad25750600061034a565b82820282848281610adf57fe5b04146103475760405162461bcd60e51b8152600401610374906124a7565b600061034783836040518060400160405280601a81526020017f536166654d6174683a206469766973696f6e206279207a65726f000000000000815250610f27565b6006546040516303ec000360e61b81526000916001600160a01b03169063fb0000c090610b70908790600401612524565b60206040518083038186803b158015610b8857600080fd5b505afa158015610b9c573d6000803e3d6000fd5b505050506040513d601f19601f82011682018060405250610bc09190810190611e3f565b90506000610bd4828463ffffffff610f6016565b905083811115610bf65760405162461bcd60e51b815260040161037490612447565b600654604051631d03b74d60e11b81526001600160a01b0390911690633a076e9a90610c289088908590600401612533565b600060405180830381600087803b158015610c4257600080fd5b505af1158015610c56573d6000803e3d6000fd5b505050505050505050565b6000610c73838363ffffffff610f8516565b90508083013414610abe5760405162461bcd60e51b8152600401610374906124f7565b60006003836004811115610ca657fe5b1480610cbd57506004836004811115610cbb57fe5b145b8015610ce957506003826004811115610cd257fe5b1480610ce957506004826004811115610ce757fe5b145b15610cf65750600061034a565b816004811115610d0257fe5b836004811115610d0e57fe5b1115610d1c5750600261034a565b50600192915050565b83610d3b57610d3688888888610f9d565b6106c3565b6106c3888888888787876111f0565b825160408082015160208082015191518188015185516060870151805190850151878c01519590980151965195976001600160a01b03909416967fdddcdb07e460849cf04a4445b7af9faf01b7f5c7ba75deaf969ac5ed830312c396610dbc9694959394929392918b918d919061257c565b60405180910390a3505050565b6001600160a01b038116610def5760405162461bcd60e51b815260040161037490612467565b600080546040516001600160a01b03808516939216917f8be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e091a3600080546001600160a01b0319166001600160a01b0392909216919091179055565b600060608590506060610ec36040518060400160405280601a81526020017f19457468657265756d205369676e6564204d6573736167653a0a000000000000815250610e9684516114f5565b604080516000808252602082018181528284018281526060840192835260808401909452889390916115b6565b90506001818051906020012087878760405160008152602001604052604051610eef9493929190612379565b6020604051602081039080840390855afa158015610f11573d6000803e3d6000fd5b5050604051601f19015198975050505050505050565b60008183610f485760405162461bcd60e51b81526004016103749190612436565b506000838581610f5457fe5b049150505b9392505050565b6000828201838110156103475760405162461bcd60e51b815260040161037490612477565b600061034761271061052f858563ffffffff610ac316565b600084604001516004811115610faf57fe5b1415610ff45760405181906001600160a01b0382169085156108fc029086906000818181858888f19350505050158015610fed573d6000803e3d6000fd5b50506111ea565b60018460400151600481111561100657fe5b141561109b5760208401511561102e5760405162461bcd60e51b8152600401610374906124e7565b600554845160405163776062c360e01b81526001600160a01b039092169163776062c3916110649186908690899060040161240e565b600060405180830381600087803b15801561107e57600080fd5b505af1158015611092573d6000803e3d6000fd5b505050506111ea565b6003846040015160048111156110ad57fe5b141561110e57826001146110d35760405162461bcd60e51b815260040161037490612457565b60035484516020860151604051637b84dc8360e11b81526001600160a01b039093169263f709b906926110649290918791879160040161240e565b60048460400151600481111561112057fe5b141561117f57826001146111465760405162461bcd60e51b815260040161037490612457565b60048054855160208701516040516321143af960e21b81526001600160a01b0390931693638450ebe4936110649392889288920161240e565b60035484516020860151604051639c1c2ee960e01b81526001600160a01b0390931692639c1c2ee9926111bc929091879187918a906004016123b0565b600060405180830381600087803b1580156111d657600080fd5b505af11580156106c3573d6000803e3d6000fd5b50505050565b60006111ff888789878761185c565b905060028260400151600481111561121357fe5b1480156112a1575081516040516301ffc9a760e01b81526001600160a01b03909116906301ffc9a79061125190632dde656160e21b90600401612394565b60206040518083038186803b15801561126957600080fd5b505afa15801561127d573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052506112a19190810190611ca1565b8061136157506003826040015160048111156112b957fe5b14806112d457506004826040015160048111156112d257fe5b145b8015611361575081516040516301ffc9a760e01b81526001600160a01b03909116906301ffc9a79061131190632dde656160e21b90600401612394565b60206040518083038186803b15801561132957600080fd5b505afa15801561133d573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052506113619190810190611ca1565b156114e8578151602083015160405163b9c4d9fb60e01b81526060916001600160a01b0384169163b9c4d9fb9161139a9160040161256e565b60006040518083038186803b1580156113b257600080fd5b505afa1580156113c6573d6000803e3d6000fd5b505050506040513d6000823e601f3d908101601f191682016040526113ee9190810190611c37565b90506060826001600160a01b0316630ebd4c7f86602001516040518263ffffffff1660e01b8152600401611422919061256e565b60006040518083038186803b15801561143a57600080fd5b505afa15801561144e573d6000803e3d6000fd5b505050506040513d6000823e601f3d908101601f191682016040526114769190810190611c6c565b9050815181511461148657600080fd5b60005b81518110156114e3576000806114b3878e8686815181106114a657fe5b60200260200101516118ca565b915091508196506114d98e828e8887815181106114cc57fe5b6020026020010151610f9d565b5050600101611489565b505050505b84610c5689838984610f9d565b60608161151a57506040805180820190915260018152600360fc1b602082015261032a565b8160005b811561153257600101600a8204915061151e565b6060816040519080825280601f01601f19166020018201604052801561155f576020820181803883390190505b50905060001982015b85156115ad57600a860660300160f81b8282806001900393508151811061158b57fe5b60200101906001600160f81b031916908160001a905350600a86049550611568565b50949350505050565b60608082518451865188518a518c518e510101010101016040519080825280601f01601f1916602001820160405280156115f7576020820181803883390190505b5090506000805b8a5181101561164f578a818151811061161357fe5b602001015160f81c60f81b83838060010194508151811061163057fe5b60200101906001600160f81b031916908160001a9053506001016115fe565b5060005b89518110156116a45789818151811061166857fe5b602001015160f81c60f81b83838060010194508151811061168557fe5b60200101906001600160f81b031916908160001a905350600101611653565b5060005b88518110156116f9578881815181106116bd57fe5b602001015160f81c60f81b8383806001019450815181106116da57fe5b60200101906001600160f81b031916908160001a9053506001016116a8565b5060005b875181101561174e5787818151811061171257fe5b602001015160f81c60f81b83838060010194508151811061172f57fe5b60200101906001600160f81b031916908160001a9053506001016116fd565b5060005b86518110156117a35786818151811061176757fe5b602001015160f81c60f81b83838060010194508151811061178457fe5b60200101906001600160f81b031916908160001a905350600101611752565b5060005b85518110156117f8578581815181106117bc57fe5b602001015160f81c60f81b8383806001019450815181106117d957fe5b60200101906001600160f81b031916908160001a9053506001016117a7565b5060005b845181101561184d5784818151811061181157fe5b602001015160f81c60f81b83838060010194508151811061182e57fe5b60200101906001600160f81b031916908160001a9053506001016117fc565b50909998505050505050505050565b600080600061186c8687876118ca565b90925090506000611883878663ffffffff610f8516565b90506000611897828463ffffffff610f6016565b905080156118ba576001546118ba908b9083908c906001600160a01b0316610f9d565b5091925050505b95945050505050565b6000806118e6856118e1868663ffffffff610f8516565b6118f2565b91509150935093915050565b600080828411156119085750508082038161190f565b5060009050825b9250929050565b803561034a81612712565b805161034a81612712565b600082601f83011261193d57600080fd5b815161195061194b8261261a565b6125f3565b9150818183526020840193506020810190508385602084028201111561197557600080fd5b60005b838110156119a1578161198b8882611921565b8452506020928301929190910190600101611978565b5050505092915050565b600082601f8301126119bc57600080fd5b81516119ca61194b8261261a565b915081818352602084019350602081019050838560208402820111156119ef57600080fd5b60005b838110156119a15781611a058882611bfb565b84525060209283019291909101906001016119f2565b805161034a81612726565b803561034a8161272f565b803561034a81612738565b600060608284031215611a4e57600080fd5b611a5860606125f3565b90506000611a668484611916565b8252506020611a7784848301611a26565b6020830152506040611a8b84828501611a31565b60408301525092915050565b60006101008284031215611aaa57600080fd5b50919050565b60006101008284031215611ac357600080fd5b611acd60806125f3565b90506000611adb8484611916565b8252506020611aec84848301611a26565b6020830152506040611b0084828501611a3c565b60408301525060a0611b1484828501611a3c565b60608301525092915050565b60006101608284031215611aaa57600080fd5b60006101608284031215611b4657600080fd5b611b5060806125f3565b90506000611b5e8484611ab0565b825250610100611b7084848301611a26565b602083015250610120611b8584828501611a26565b604083015250610140611b1484828501611a26565b600060608284031215611aaa57600080fd5b600060608284031215611bbe57600080fd5b611bc860606125f3565b90506000611bd68484611c06565b8252506020611be784848301611a26565b6020830152506040611a8b84828501611a26565b805161034a8161272f565b803561034a81612745565b600060208284031215611c2357600080fd5b6000611c2f8484611916565b949350505050565b600060208284031215611c4957600080fd5b815167ffffffffffffffff811115611c6057600080fd5b611c2f8482850161192c565b600060208284031215611c7e57600080fd5b815167ffffffffffffffff811115611c9557600080fd5b611c2f848285016119ab565b600060208284031215611cb357600080fd5b6000611c2f8484611a1b565b600060208284031215611cd157600080fd5b6000611c2f8484611a31565b600060608284031215611cef57600080fd5b6000611c2f8484611a3c565b60006101008284031215611d0e57600080fd5b6000611c2f8484611a97565b60006101008284031215611d2d57600080fd5b6000611c2f8484611ab0565b6000806000806000806102808789031215611d5357600080fd5b6000611d5f8989611b20565b965050610160611d7189828a01611b9a565b9550506101c0611d8389828a01611a26565b9450506101e0611d9589828a01611b9a565b935050610240611da789828a01611a26565b925050610260611db989828a01611916565b9150509295509295509295565b60006101608284031215611dd957600080fd5b6000611c2f8484611b33565b6000806101808385031215611df957600080fd5b6000611e058585611b33565b925050610160611e1785828601611a26565b9150509250929050565b600060608284031215611e3357600080fd5b6000611c2f8484611bac565b600060208284031215611e5157600080fd5b6000611c2f8484611bfb565b611e66816126b5565b82525050565b611e6681612679565b611e6681612684565b611e6681612689565b611e668161268c565b611e66816126bc565b611e66816126c7565b6000611ead8261263b565b611eb7818561263f565b9350611ec78185602086016126d2565b611ed0816126fe565b9093019392505050565b6000611ee760248361263f565b7f6e6f7420656e6f7567682073746f636b206f66206f7264657220666f7220627581526379696e6760e01b602082015260400192915050565b6000611f2d601d8361263f565b7f76616c75652073686f756c64206265203120666f72204552432d373231000000815260200192915050565b6000611f6660268361263f565b7f4f776e61626c653a206e6577206f776e657220697320746865207a65726f206181526564647265737360d01b602082015260400192915050565b6000611fae601b8361263f565b7f536166654d6174683a206164646974696f6e206f766572666c6f770000000000815260200192915050565b6000611fe760138361263f565b72696e636f7272656374207369676e617475726560681b815260200192915050565b6000612016601d8361263f565b7f696e636f727265637420627579657220666565207369676e6174757265000000815260200192915050565b600061204f60218361263f565b7f536166654d6174683a206d756c7469706c69636174696f6e206f766572666c6f8152607760f81b602082015260400192915050565b600061209260208361263f565b7f4f776e61626c653a2063616c6c6572206973206e6f7420746865206f776e6572815260200192915050565b60006120cb60218361263f565b7f455448206973206e6f7420737570706f72746564206f6e2073656c6c207369648152606560f81b602082015260400192915050565b600061034a60008361263f565b600061211b600c8361263f565b6b3737ba1030b71037bbb732b960a11b815260200192915050565b600061214360138361263f565b720746f6b656e49642073686f756c64206265203606c1b815260200192915050565b600061217260168361263f565b751b5cd9cb9d985b1d59481a5cc81a5b98dbdc9c9958dd60521b815260200192915050565b606082016121a58280612648565b6121af8482611e6c565b506121bd602083018361266a565b6121ca6020850182611e7e565b506121d86040830183612657565b6111ea6040850182611e99565b805160608301906121f68482611e6c565b5060208201516122096020850182611e7e565b5060408201516111ea6040850182611e99565b610100820161222b8280612648565b6122358482611e6c565b50612243602083018361266a565b6122506020850182611e7e565b5061225e6040830183612666565b61226b6040850182612197565b5061227960a0830183612666565b6111ea60a0850182612197565b80516101008301906122988482611e6c565b5060208201516122ab6020850182611e7e565b5060408201516122be60408501826121e5565b5060608201516111ea60a08501826121e5565b80516101608301906122e38482612286565b5060208201516122f7610100850182611e7e565b50604082015161230b610120850182611e7e565b5060608201516111ea610140850182611e7e565b611e66816126af565b6020810161034a8284611e6c565b608081016123448287611e5d565b6123516020830186611e6c565b61235e6040830185611e7e565b6118c16060830184611e7e565b6020810161034a8284611e75565b608081016123878287611e7e565b612351602083018661231f565b6020810161034a8284611e87565b6020810161034a8284611e90565b60c081016123be8288611e90565b6123cb6020830187611e6c565b6123d86040830186611e6c565b6123e56060830185611e7e565b6123f26080830184611e7e565b81810360a083015261240381612101565b979650505050505050565b6080810161241c8287611e90565b6124296020830186611e6c565b61235e6040830185611e6c565b602080825281016103478184611ea2565b6020808252810161032781611eda565b6020808252810161032781611f20565b6020808252810161032781611f59565b6020808252810161032781611fa1565b6020808252810161032781611fda565b6020808252810161032781612009565b6020808252810161032781612042565b6020808252810161032781612085565b60208082528101610327816120be565b602080825281016103278161210e565b6020808252810161032781612136565b6020808252810161032781612165565b6101208101612516828561221c565b610f59610100830184611e7e565b610100810161034a8284612286565b61012081016125168285612286565b610160810161034a82846122d1565b610180810161256082856122d1565b610f59610160830184611e7e565b6020810161034a8284611e7e565b610100810161258b828b611e7e565b612598602083018a611e6c565b6125a56040830189611e6c565b6125b26060830188611e7e565b6125bf6080830187611e7e565b6125cc60a0830186611e6c565b6125d960c0830185611e7e565b6125e660e0830184611e7e565b9998505050505050505050565b60405181810167ffffffffffffffff8111828210171561261257600080fd5b604052919050565b600067ffffffffffffffff82111561263157600080fd5b5060209081020190565b5190565b90815260200190565b60006103476020840184611916565b60006103476020840184611a31565b5090565b60006103476020840184611a26565b6000610327826126a3565b151590565b90565b6001600160e01b03191690565b8061032a81612708565b6001600160a01b031690565b60ff1690565b6000610327825b600061032782612679565b600061032782612699565b60005b838110156126ed5781810151838201526020016126d5565b838111156111ea5750506000910152565b601f01601f191690565b6005811061081757fe5b61271b81612679565b811461081757600080fd5b61271b81612684565b61271b81612689565b6005811061081757600080fd5b61271b816126af56fea365627a7a72315820559aa28e28a92b415b945f053776f237c6d6a776c69f445be4800b11f7d401db6c6578706572696d656e74616cf564736f6c63430005110040"}}""", JsonSerializerOptions.Default)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        JToken.Parse(serialized).Should().BeEquivalentTo("""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"0x"},"id":67}""");
    }

    private static async Task TestEstimateGasOutOfGas(Context ctx, long? specifiedGasLimit, long expectedGasLimit, string message)
    {
        string gasParam = specifiedGasLimit.HasValue ? $", \"gas\": \"0x{specifiedGasLimit.Value:X}\"" : "";
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\"{gasParam}, \"data\": \"{InfiniteLoopCode.ToHexString(true)}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        JToken.Parse(serialized).Should().BeEquivalentTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32000,\"message\":\"{message}\"}},\"id\":67}}");
    }

}
