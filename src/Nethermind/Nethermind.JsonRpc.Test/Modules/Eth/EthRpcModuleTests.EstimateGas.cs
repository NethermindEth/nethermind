// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Blockchain;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Text;
using Nethermind.Abi;
using Nethermind.Core.Messages;

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
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"insufficient sender balance for transfer\"},\"id\":67}"));
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

    private static IEnumerable<TestCaseData> CreateAccessListGasCases()
    {
        yield return new TestCaseData(false, 2, Berlin.Instance).SetName("Berlin: noOpt, 2");
        yield return new TestCaseData(true, 2, Berlin.Instance).SetName("Berlin: opt, 2");
        yield return new TestCaseData(true, 17, Berlin.Instance).SetName("Berlin: opt, 17");
        yield return new TestCaseData(false, 2, Eip7981Spec).SetName("EIP-7981: noOpt, 2");
        yield return new TestCaseData(true, 2, Eip7981Spec).SetName("EIP-7981: opt, 2");
        yield return new TestCaseData(true, 17, Eip7981Spec).SetName("EIP-7981: opt, 17");
    }

    [TestCaseSource(nameof(CreateAccessListGasCases))]
    public async Task Eth_create_access_list_calculates_proper_gas(bool optimize, long loads, IReleaseSpec spec)
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(spec));

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

        Assert.That(optimizedEstimateGas, Is.LessThan(estimateGas));
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

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(resultOverrideBefore), Is.EqualTo(JToken.Parse(resultOverrideAfter)).Using(JToken.EqualityComparer));
            Assert.That(JToken.Parse(resultNoOverride), Is.Not.EqualTo(JToken.Parse(resultOverrideAfter)).Using(JToken.EqualityComparer));
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

        // gasCap above blockGasLimit — estimate should be bounded by blockGasLimit, not gasCap (matches Geth)
        ctx.Test.RpcConfig.GasCap = (ulong)(blockGasLimit + 1_000_000);

        await TestEstimateGasOutOfGas(ctx, null, blockGasLimit, $"gas required exceeds allowance ({blockGasLimit})");
    }

    [Test]
    public async Task Estimate_gas_treats_zero_gas_as_not_specified()
    {
        using Context ctx = await Context.Create();

        string blockNumberResponse = await ctx.Test.TestEthRpc("eth_blockNumber");
        string blockNumber = JToken.Parse(blockNumberResponse).Value<string>("result")!;
        string blockResponse = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockNumber, false);
        long blockGasLimit = Convert.ToInt64(JToken.Parse(blockResponse).SelectToken("result.gasLimit")!.Value<string>(), 16);

        ctx.Test.RpcConfig.GasCap = (ulong)(blockGasLimit + 1_000_000);

        await TestEstimateGasOutOfGas(ctx, 0, blockGasLimit, $"gas required exceeds allowance ({blockGasLimit})");
    }

    [Test]
    public async Task Estimate_gas_with_explicit_zero_gas_limit_simple_transfer()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gas\": \"0x0\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
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
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","error":{"code":-32000,"message":"gas required exceeds allowance (351)"},"id":67}""")).Using(JToken.EqualityComparer));
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

        object transaction = JsonSerializer.Deserialize<object>(
            $"{{\"from\":\"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700\",\"gasPrice\":\"0xBA43B7400\",\"data\":\"{BalanceOfCallData}\",\"to\":\"{BatTokenAddress}\"}}",
            JsonSerializerOptions.Default)!;
        object stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x16345785D8A0000"}}""",
            JsonSerializerOptions.Default)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":"0x53b8","id":67}""")).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_estimateGas_returns_execution_reverted_when_gas_price_set_and_contract_reverts()
    {
        // Regression for: balance < explicit_gas × gasPrice, but the EVM should still run and surface the revert.
        // Before fix: TransactionProcessor rejected with "insufficient MaxFeePerGas for sender balance" before EVM ran.
        // explicit_gas(0xE234=57908) × gasPrice(50Gwei) ≈ 0.0029 ETH > balance(0.002 ETH).
        // The target contract reverts unconditionally; after the fix estimation returns "execution reverted".
        using Context ctx = await Context.CreateWithLondonEnabled();

        object transaction = JsonSerializer.Deserialize<object>(
            """{"from":"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gas":"0xE234","gasPrice":"0xBA43B7400"}""",
            JsonSerializerOptions.Default)!;
        // balance = 0.002 ETH (below gas × gasPrice = 0.0029 ETH but above intrinsicGas × gasPrice)
        // target address has minimal always-revert bytecode: PUSH1 0, PUSH1 0, REVERT
        object stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x71AFD498D0000"},"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1":{"code":"0x60006000fd"}}""",
            JsonSerializerOptions.Default)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"0x"},"id":67}""")).Using(JToken.EqualityComparer));
    }

    private static IEnumerable<TestCaseData> EstimateGasLowerBoundFollowUpCases()
    {
        yield return new TestCaseData(
                """{"from":"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gas":"0x3e8","gasPrice":"0xBA43B7400","data":"0xa9059cbb0000000000000000000000004debb0df4da8d1f51ef67b727c3f1c0ecc7ed00900000000000000000000000000000000000000000000000000000000000f4240"}""",
                """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x56bc75e2d63100000"},"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1":{"code":"0x60006000fd"}}""",
                """{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"0x"},"id":67}""")
            .SetName("Eth_estimateGas_lower_bound_probe_follow_up_revert_wins");

        yield return new TestCaseData(
                """{"from":"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gas":"0x3e8","gasPrice":"0xBA43B7400","value":"0x1"}""",
                """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x56bc75e2d63100000"}}""",
                """{"jsonrpc":"2.0","result":"0x5208","id":67}""")
            .SetName("Eth_estimateGas_lower_bound_probe_follow_up_success_wins");

        yield return new TestCaseData(
                """{"from":"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gas":"0x5208","gasPrice":"0xBA43B7400","value":"0x1"}""",
                """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x44867db30"}}""",
                """{"jsonrpc":"2.0","error":{"code":-32000,"message":"gas required exceeds allowance (0)"},"id":67}""")
            .SetName("Eth_estimateGas_lower_bound_probe_follow_up_allowance_wins");
    }

    [TestCaseSource(nameof(EstimateGasLowerBoundFollowUpCases))]
    public async Task Eth_estimateGas_lower_bound_probe_follow_up_cases(string transactionJson, string stateOverrideJson, string expectedJson)
    {
        // Regression for #11768 follow-up: when the first probe is only a lower-bound artifact,
        // the final result should reflect the later estimate path rather than leaking the early probe error.
        using Context ctx = await Context.CreateWithLondonEnabled();

        object transaction = JsonSerializer.Deserialize<object>(transactionJson, JsonSerializerOptions.Default)!;
        object stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson, JsonSerializerOptions.Default)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        Assert.That(serialized, Is.EqualTo(expectedJson));
    }

    private static readonly OverridableReleaseSpec Eip7976Spec = new(Prague.Instance) { IsEip7976Enabled = true };
    private static readonly OverridableReleaseSpec Eip7981Spec = new(Amsterdam.Instance) { IsEip7976Enabled = true, IsEip7981Enabled = true };

    private static IEnumerable<TestCaseData> EstimateGasFloorCostCases()
    {
        // EIP-7976: 100 zero bytes → floor = 21000 + 100 * 4 * 16 = 27400
        ulong eip7976Floor100 = GasCostOf.Transaction + 100 * Eip7976Spec.GasCosts.TxDataNonZeroMultiplier * Eip7976Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7976Spec, new byte[100], 100_000L, null,
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7976Floor100.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7976: data heavy tx returns floor cost");

        // EIP-7623: 100 zero bytes → floor = 21000 + 100 * 10 = 22000
        ulong eip7623Floor100 = GasCostOf.Transaction + 100 * Prague.Instance.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Prague.Instance, new byte[100], 100_000L, null,
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7623Floor100.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7623: data heavy tx returns lower floor");

        // EIP-7976: gas at standard but below floor → "gas below floor data cost"
        // 1 zero byte: standard = 21000 + 4 = 21004; floor = 21000 + 1*4*16 = 21064
        const long atStandard = (long)(GasCostOf.Transaction + GasCostOf.TxDataZero);
        ulong eip7976Floor1Byte = GasCostOf.Transaction + 1 * Eip7976Spec.GasCosts.TxDataNonZeroMultiplier * Eip7976Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7976Spec, new byte[] { 0 }, atStandard, null,
                $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32000,\"message\":\"failed with {atStandard} gas: gas below floor data cost: have {atStandard}, want {eip7976Floor1Byte}\"}},\"id\":67}}")
            .SetName("EIP-7976: gas at standard but below floor returns floor error");

        // EIP-7976: mixed calldata (0x00001122 = 2 zero + 2 nonzero bytes)
        ulong eip7976Floor4 = GasCostOf.Transaction + 4 * Eip7976Spec.GasCosts.TxDataNonZeroMultiplier * Eip7976Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7976Spec, new byte[] { 0x00, 0x00, 0x11, 0x22 }, 100_000L, null,
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7976Floor4.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7976: mixed calldata returns floor");

        // EIP-7981: access list with 1 address, no calldata — standard wins. Devnet-6 prices the
        // access-list address at the spec's AccessAccountListEntry (the EIP-8038 cold cost), not the
        // legacy EIP-2930 value, plus the 80 access-list floor tokens.
        ulong eip7981Standard = GasCostOf.Transaction + Eip8038Constants.AccessListAddressCost
            + 80 * Eip7981Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7981Spec, Array.Empty<byte>(), 100_000L,
                new AccessList.Builder().AddAddress(Address.Zero).Build(),
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7981Standard.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7981: standard wins with access list");

        // EIP-7981: modest calldata (100 zero bytes) on top of an access list — the standard cost
        // (TX_BASE + access-entry + per-zero-byte standard cost + access floor tokens) still exceeds
        // the calldata floor, so standard wins. (Floor-wins is covered by the data-heavy cases above.)
        ulong eip7981StandardWithCalldata = GasCostOf.Transaction + Eip8038Constants.AccessListAddressCost
            + 100 * GasCostOf.TxDataZero
            + 80 * Eip7981Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7981Spec, new byte[100], 100_000L,
                new AccessList.Builder().AddAddress(Address.Zero).Build(),
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7981StandardWithCalldata.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7981: standard wins with calldata and access list");
    }

    [TestCaseSource(nameof(EstimateGasFloorCostCases))]
    public async Task Eth_estimateGas_floor_cost(IReleaseSpec spec, byte[] data, long gasLimit, AccessList? accessList, string expectedJson)
    {
        TestSpecProvider specProvider = new(spec);
        using Context ctx = await Context.Create(specProvider);

        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit((ulong)gasLimit)
            .WithData(data);
        if (accessList is not null)
            txBuilder.WithAccessList(accessList);
        Transaction tx = txBuilder.SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = null;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(serialized, Is.EqualTo(expectedJson));
    }

    private static async Task TestEstimateGasOutOfGas(Context ctx, long? specifiedGasLimit, long expectedGasLimit, string message)
    {
        string gasParam = specifiedGasLimit.HasValue ? $", \"gas\": \"0x{specifiedGasLimit.Value:X}\"" : "";
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\"{gasParam}, \"data\": \"{InfiniteLoopCode.ToHexString(true)}\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32000,\"message\":\"{message}\"}},\"id\":67}}")).Using(JToken.EqualityComparer));
    }


    [Test]
    public async Task Estimate_gas_baseFeePerGas_override_allows_tx_with_gasPrice_below_real_baseFee()
    {
        // In London the block has a non-zero baseFee (≥ 1 gwei).
        // A legacy tx with explicit gasPrice=1 wei fails because ShouldSetBaseFee() is true
        // (gasPrice is set) and gasPrice < baseFee.
        // With baseFeePerGas=0 override the check passes and the tx can be estimated.
        // A state override funds the sender so balance is not the limiting factor.
        using Context ctx = await Context.CreateWithLondonEnabled();

        string sender = TestItem.AddressA.ToString();
        object? transaction = JsonSerializer.Deserialize<object>(
            "{\"from\":\"" + sender + "\",\"to\":\"0xc200000000000000000000000000000000000000\",\"gasPrice\":\"0x1\"}");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            "{\"" + sender + "\":{\"balance\":\"0xde0b6b3a7640000\"}}"); // 1 ETH

        string withoutOverride = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
        Assert.That(JToken.Parse(withoutOverride)["error"], Is.Not.Null, "gasPrice(1 wei) < baseFee should fail without block override");

        object? blockOverride = JsonSerializer.Deserialize<object>("""{"baseFeePerGas":"0x0"}""");
        string withOverride = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride, blockOverride);
        Assert.That(JToken.Parse(withOverride)["result"]!.Value<string>(), Is.EqualTo("0x5208"));
    }

    [Test]
    public async Task Estimate_gas_block_override_gasLimit_bounds_estimation()
    {
        // blockOverride.gasLimit=50000 caps the gas budget.
        // Contract creation always costs at least 21000 (intrinsic) + 32000 (TxCreate) = 53000,
        // which exceeds the 50000 cap.
        using Context ctx = await Context.CreateWithCancunEnabled();

        // Bytecode from the equivalent geth test: constructor that checks basefee/gasprice.
        const string initBytecode = "0x6080604052348015600f57600080fd5b50483a1015601c57600080fd5b60003a111560315760004811603057600080fd5b5b603f80603e6000396000f3fe6080604052600080fdfea264697066735822122060729c2cee02b10748fae5200f1c9da4661963354973d9154c13a8e9ce9dee1564736f6c63430008130033";
        object? transaction = JsonSerializer.Deserialize<object>(
            "{\"from\":\"" + TestItem.AddressA + "\",\"data\":\"" + initBytecode + "\"}");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            "{\"" + TestItem.AddressA + "\":{\"balance\":\"0xde0b6b3a7640000\"}}"); // 1 ETH
        object? blockOverride = JsonSerializer.Deserialize<object>("""{"gasLimit":"0xC350"}"""); // 50000

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride, blockOverride);
        Assert.That(JToken.Parse(serialized)["error"]!["message"]!.Value<string>(),
            Does.StartWith("Cannot estimate gas"));
    }

    [TestCase(
        "Sufficient balance succeeds",
        """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","type":"0x3","maxFeePerGas":"0x3B9ACA00","maxPriorityFeePerGas":"0x3B9ACA00","maxFeePerBlobGas":"0x3B9ACA00","blobVersionedHashes":["0x0122000000000000000000000000000000000000000000000000000000000000"]}""",
        """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x7700000000002","nonce":"0x0"}}""",
        """{"jsonrpc":"2.0","result":"0x5208","id":67}"""
    )]
    [TestCase(
        // 6 blobs × 131072 × 1 Gwei = 786,432,000,000,000 wei in blob fees — balance covers both.
        "6 blobs with sufficient balance succeeds",
        """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","type":"0x3","maxFeePerGas":"0x3B9ACA00","maxPriorityFeePerGas":"0x3B9ACA00","maxFeePerBlobGas":"0x3B9ACA00","blobVersionedHashes":["0x0122000000000000000000000000000000000000000000000000000000000000","0x0123000000000000000000000000000000000000000000000000000000000000","0x0124000000000000000000000000000000000000000000000000000000000000","0x0125000000000000000000000000000000000000000000000000000000000000","0x0126000000000000000000000000000000000000000000000000000000000000","0x0127000000000000000000000000000000000000000000000000000000000000"]}""",
        """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x7700000000002","nonce":"0x0"}}""",
        """{"jsonrpc":"2.0","result":"0x5208","id":67}"""
    )]
    [TestCase(
        // Sender has 100 wei — enough for value=0 but not for blob gas:
        // blobUsage = 1 blob × 131072 gas × 1 Gwei = 131,072,000,000,000 wei >> 100 wei.
        "Insufficient balance for blob gas fails",
        """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","type":"0x3","gas":"0x1C9C380","maxFeePerGas":"0x3B9ACA00","maxPriorityFeePerGas":"0x3B9ACA00","maxFeePerBlobGas":"0x3B9ACA00","blobVersionedHashes":["0x0122000000000000000000000000000000000000000000000000000000000000"]}""",
        """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x64","nonce":"0x0"}}""",
        """{"jsonrpc":"2.0","error":{"code":-32000,"message":"insufficient funds for gas * price + value"},"id":67}"""
    )]
    public async Task Eth_estimateGas_blob_transaction(string name, string transactionJson, string stateOverrideJson, string expectedResult)
    {
        ISpecProvider specProvider = new TestSpecProvider(Cancun.Instance);
        Block[] blocks = [Build.A.Block.WithNumber(0).WithGasLimit(30_000_000).WithExcessBlobGas(1ul).TestObject];
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        using TestRpcBlockchain test = await TestRpcBlockchain
            .ForTest(SealEngineType.NethDev)
            .WithBlockFinder(blockTree)
            .Build(specProvider);

        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        string serialized = await test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_estimateGas_blob_transaction_rejected_when_blobBaseFee_block_override_exceeds_maxFeePerBlobGas()
    {
        // excessBlobGas=1 so the static calculator gives feePerBlobGas=1 (would pass),
        // confirming the decorated calculator path is needed to enforce the override (11 > 10).
        ISpecProvider specProvider = new TestSpecProvider(Cancun.Instance);
        Block[] blocks = [Build.A.Block.WithNumber(0).WithGasLimit(30_000_000).WithExcessBlobGas(1ul).TestObject];
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        using TestRpcBlockchain test = await TestRpcBlockchain
            .ForTest(SealEngineType.NethDev)
            .WithBlockFinder(blockTree)
            .Build(specProvider);

        object? transaction = JsonSerializer.Deserialize<object>(
            """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","type":"0x3","maxFeePerGas":"0x3B9ACA00","maxPriorityFeePerGas":"0x1","maxFeePerBlobGas":"0xa","blobVersionedHashes":["0x0122000000000000000000000000000000000000000000000000000000000000"]}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x56BC75E2D63100000","nonce":"0x0"}}""");
        object? blockOverride = JsonSerializer.Deserialize<object>("""{"blobBaseFee":"0xb","baseFeePerGas":"0x1"}""");

        string serialized = await test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride, blockOverride);

        Assert.That(
            JToken.Parse(serialized)["error"]!["message"]!.Value<string>(),
            Does.Contain("max fee per blob gas less than block blob gas fee"));
    }

    [TestCase(
        """{"from":"0x0001020304050607080910111213141516171819","to":"0x0000000000000000000000000000000000000000","value":"0x0","type":"0x4","authorizationList":[]}""",
        "failed with 4000000 gas: " + TxErrorMessages.MissingAuthorizationList + " (sender 0x0001020304050607080910111213141516171819)",
        TestName = "Empty authorization list")]
    [TestCase(
        """{"from":"0x0001020304050607080910111213141516171819","value":"0x0","type":"0x4","data":"0x60006000f3","authorizationList":[{"chainId":"0x1","address":"0x0000000000000000000000000000000000000001","nonce":"0x1","yParity":"0x0","r":"0x0101010101010101010101010101010101010101010101010101010101010101","s":"0x0101010101010101010101010101010101010101010101010101010101010101"}]}""",
        "failed with 4000000 gas: " + TxErrorMessages.NotAllowedCreateTransaction + " (sender 0x0001020304050607080910111213141516171819)",
        TestName = "Contract creation")]
    public async Task Eth_estimateGas_setCode_invalid_transaction_returns_error(string txJson, string expectedMessage)
    {
        TestSpecProvider specProvider = new(Prague.Instance);
        using Context ctx = await Context.Create(specProvider);

        object transaction = JsonSerializer.Deserialize<object>(txJson)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest");

        JToken parsed = JToken.Parse(serialized);
        Assert.That(parsed["error"]!["code"]!.Value<int>(), Is.EqualTo(-32000));
        Assert.That(parsed["error"]!["message"]!.Value<string>(), Is.EqualTo(expectedMessage));
    }

    [Test]
    public async Task Eth_estimateGas_setCode_missing_yParity_returns_error()
    {
        TestSpecProvider specProvider = new(Prague.Instance);
        using Context ctx = await Context.Create(specProvider);

        object transaction = JsonSerializer.Deserialize<object>(
            $$$"""{"from":"0x0001020304050607080910111213141516171819","to":"0x0000000000000000000000000000000000000000","type":"0x4","authorizationList":[{"chainId":"0x1","address":"{{{TestItem.AddressA}}}","nonce":"0x1","r":"0x0101010101010101010101010101010101010101010101010101010101010101","s":"0x0101010101010101010101010101010101010101010101010101010101010101"}]}""")!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest");

        JToken parsed = JToken.Parse(serialized);
        Assert.That(parsed["error"]!["code"]!.Value<int>(), Is.EqualTo(-32602));
    }

}
