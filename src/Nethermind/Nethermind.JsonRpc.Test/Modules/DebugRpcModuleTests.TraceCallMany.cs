// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Test.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[TestFixture]
public partial class DebugRpcModuleTests
{
    private static TransactionBundle CreateBundle(params TransactionForRpc[] transactions) => new() { Transactions = transactions };

    private static TransactionBundle CreateGasProbeBundle(ulong? gas = null) => new()
    {
        Transactions = [new LegacyTransactionForRpc { To = EthRpcSimulateTestsBase.GasProbeContractAddress, Gas = gas }],
        StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [EthRpcSimulateTestsBase.GasProbeContractAddress] = new()
            {
                Code = Bytes.FromHexString("0x5a60005260206000f3")
            }
        }
    };

    private static IEnumerable<TestCaseData> DebugTraceCallManyMissingGasCases()
    {
        yield return new TestCaseData((ulong?)null, (ulong?)null, false).SetName("omitted_gas_defaults_to_gas_cap_not_block_gas_limit");
        yield return new TestCaseData((ulong?)0UL, (ulong?)null, false).SetName("zero_gas_defaults_to_gas_cap_not_block_gas_limit");
        yield return new TestCaseData((ulong?)null, (ulong?)0UL, true).SetName("omitted_gas_with_zero_gas_cap_uncapped");
        yield return new TestCaseData((ulong?)0UL, (ulong?)0UL, true).SetName("zero_gas_with_zero_gas_cap_uncapped");
    }

    private static LegacyTransactionForRpc CreateTransaction(
        Address? from = null,
        Address? to = null,
        UInt256? value = null,
        ulong gas = GasCostOf.Transaction) =>
        new()
        {
            From = from ?? TestItem.AddressD,
            To = to ?? TestItem.AddressC,
            Value = value ?? 1.Ether,
            Gas = gas
        };

    private static async Task<Context> CreateContext()
    {
        Context ctx = await Context.Create();
        try
        {
            await ctx.Blockchain.AddFunds(TestItem.AddressD, 100.Ether);
        }
        catch
        {
            ctx.Dispose();
            throw;
        }

        return ctx;
    }

    [Test]
    public async Task Debug_traceCallMany_uses_generated_result_metadata([Values("0", "-1s", "1ns", "3m")] string timeout)
    {
        using Context ctx = await Context.Create();
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany",
            System.Array.Empty<TransactionBundle>(), "latest", new { timeout });

        Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task Debug_traceCallMany_with_single_bundle_single_transaction([Values] bool streaming)
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        TransactionBundle bundle = CreateBundle(CreateTransaction());

        using ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> moduleResult = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        Assert.That(moduleResult.Data is IStreamableResult, Is.EqualTo(streaming));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

#pragma warning disable NUnit2045 // The collection shape must be checked before indexing or taking its first element.
        Assert.That(result, Has.Count.EqualTo(1));
#pragma warning restore NUnit2045
        Assert.That((JArray)result[0], Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Debug_traceCallMany_executes_empty_contract_creation([Values] bool streaming, [Values] bool blockOverride)
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        object bundle = new
        {
            transactions = new[] { new { from = TestItem.AddressD, gas = "0x186a0", input = "0x" } },
            blockOverride = blockOverride ? new { number = "0x100" } : null
        };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", new[] { bundle }, "latest");
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, response);
            Assert.That((JArray?)json["result"], Has.Count.EqualTo(1), response);
            Assert.That((JArray?)json["result"]?[0], Has.Count.EqualTo(1), response);
            Assert.That((bool?)json["result"]?[0]?[0]?["failed"], Is.False, response);
            Assert.That((long?)json["result"]?[0]?[0]?["gas"], Is.EqualTo(53_000), response);
            Assert.That(json["result"]?[0]?[0]?["structLogs"], Is.Empty, response);
        }
    }

    [Test]
    public async Task Debug_traceCallMany_iterator_uses_generated_interface_metadata()
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = false;
        TransactionBundle[] bundles = [CreateBundle(CreateTransaction())];
        JArray expected = await RunTraceCallManyAsJson(ctx, bundles);
        object result = ctx.DebugRpcModule.debug_traceCallMany(bundles, BlockParameter.Latest).Data;
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine([.. options.TypeInfoResolverChain.OfType<System.Text.Json.Serialization.JsonSerializerContext>()]);
        options.MakeReadOnly();

        string actual = JsonSerializer.Serialize(result, RpcPayloadTypeInfo.Get(options, result.GetType()));

        Assert.That(JToken.DeepEquals(JToken.Parse(actual), expected), Is.True, actual);
    }

    [Test]
    public async Task Debug_traceCallMany_ignores_traceCall_transaction_index([Values] bool streaming, [Values(0UL, ulong.MaxValue)] ulong index)
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        TransactionBundle[] bundles = [CreateBundle(CreateTransaction())];
        JArray expected = await RunTraceCallManyAsJson(ctx, bundles);
        JArray actual = await RunTraceCallManyAsJson(ctx, bundles, new GethTraceOptions { TxIndex = index });
        Assert.That(JToken.DeepEquals(actual, expected), Is.True, actual.ToString());
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_bundles()
    {
        using Context ctx = await CreateContext();

        JArray result = await RunTraceCallManyAsJson(ctx, [CreateBundle(CreateTransaction()), CreateBundle(CreateTransaction(to: TestItem.AddressD))]);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([1, 1]));
    }

    [Test]
    public async Task Debug_traceCallMany_without_overrides_traces_each_call_against_the_base_state([Values] bool streaming)
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Address relay = TestItem.GetRandomAddress();
        TransactionBundle bundle = CreateBundle(CreateTransaction(to: relay), CreateTransaction(from: relay));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result.Select(static r => r.Select(static trace => (bool)trace["failed"]!)), Is.EqualTo(new[] { new[] { false, true } }), result.ToString());
    }

    [Test]
    public async Task Debug_traceCallMany_fails_when_not_enough_balance()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(value: 200.Ether));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([1]));

        JToken trace = result[0][0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)trace["failed"]!, Is.True, "insufficient balance must surface as failed:true");
            Assert.That((long)trace["gas"]!, Is.GreaterThan(0), "failed trace gas reflects the tx gas limit");
            Assert.That((string)trace["error"]!, Does.Contain("insufficient funds"), "Nethermind wording is translated to Geth's wording for compat");
            Assert.That((int)trace["errorCode"]!, Is.EqualTo(ErrorCodes.InvalidInput), "tracing-failure errorCode mirrors the buffered ErrorCodes.InvalidInput");
        }
    }

    [Test]
    public async Task Debug_traceCallMany_respects_gas_cap()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Debug_traceCallMany_with_trace_options()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        GethTraceOptions options = new() { DisableStorage = true, DisableStack = true };

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle], options);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([1]));
    }

    [Test]
    public async Task Debug_traceCallMany_to_async_stream()
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = true;

        // Multiple bundles so FlushBetweenBundles runs more than once.
        TransactionBundle[] bundles = [CreateBundle(CreateTransaction()), CreateBundle(CreateTransaction(to: TestItem.AddressD))];
        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany(bundles, BlockParameter.Latest);
        Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());
        IStreamableResult streaming = (IStreamableResult)result.Data;

        await using AsyncCompletingStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);

        await Assert.DoesNotThrowAsync(async () => await streaming.WriteToAsync(writer, CancellationToken.None));

        await writer.CompleteAsync();
    }

    private static async Task<JArray> RunTraceCallManyAsJson(Context ctx, TransactionBundle[] bundles, GethTraceOptions? options = null)
    {
        string response = options is null
            ? await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", bundles, "latest")
            : await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", bundles, "latest", options);
        return (JArray)JToken.Parse(response)["result"]!;
    }

    [Test]
    public async Task Debug_traceCallMany_with_state_override()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressD] = new() { Balance = 100.Ether }
        };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
#pragma warning disable NUnit2045 // The nested collection shape must be checked before taking its first element.
        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1]));
#pragma warning restore NUnit2045
        Assert.That(result.Data.First().First(), Is.Not.Null);
    }

    [Test]
    public async Task Debug_traceCallMany_with_combined_overrides()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());

        bundle.BlockOverride = new BlockOverride { GasLimit = 50000000 };
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressD] = new() { Balance = 100.Ether }
        };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

#pragma warning disable NUnit2045 // The nested collection shape must be checked before taking its first element.
        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1]));
#pragma warning restore NUnit2045
        Assert.That(result.Data.First().First(), Is.Not.Null);
    }

    // A bundle with overrides runs through the simulate executor, which must still reject a lone blob fee cap: only the
    // simulate endpoints read one as absent.
    [Test]
    public async Task Debug_traceCallMany_rejects_a_lone_blob_fee_cap([Values] bool blockOverride)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        object call = new { from = TestItem.AddressA.ToString(), to = TestItem.AddressB.ToString(), gas = "0x5208", maxFeePerBlobGas = "0x0" };
        object bundle = blockOverride
            ? new { transactions = new[] { call }, blockOverride = new { } }
            : new { transactions = new[] { call } };

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", new[] { bundle }, "latest");

        Assert.That(response, Does.Contain(RpcTransactionErrors.BlobFeeCapWithoutBlobHashes));
    }

    [Test]
    public async Task Debug_traceCallMany_rejects_conflicting_storage_overrides([Values] bool inOptions, [Values] bool loneBlobFeeCap)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        Dictionary<string, object> call = new() { ["from"] = TestItem.AddressA.ToString(), ["to"] = TestItem.AddressB.ToString(), ["gas"] = "0x5208" };
        if (loneBlobFeeCap) call["maxFeePerBlobGas"] = "0x0";
        object stateOverrides = new Dictionary<string, object> { [TestItem.AddressC.ToString()] = new { state = new { }, stateDiff = new { } } };

        string response = inOptions
            ? await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", new[] { new { transactions = new[] { call } } }, "latest", new { stateOverrides })
            : await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", new[] { new { transactions = new[] { call }, stateOverrides } }, "latest");

        JToken? error = JToken.Parse(response)["error"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That(error?["message"]?.Value<string>(), Is.EqualTo($"account {TestItem.AddressC.ToString(withEip55Checksum: true)} has both 'state' and 'stateDiff'"), response);
        }
    }

    [Test]
    public async Task Debug_traceCallMany_applies_a_lone_storage_override([Values] bool inOptions, [Values("state", "stateDiff", null)] string? field)
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(from: TestItem.AddressA, value: 0, gas: 100_000));
        Dictionary<UInt256, Hash256> storage = new() { [UInt256.Zero] = Keccak.OfAnEmptyString };
        Dictionary<Address, AccountOverride> overrides = new()
        {
            // PUSH1 0 SLOAD ISZERO PUSH1 8 JUMPI STOP JUMPDEST PUSH1 0 DUP1 REVERT: reverts while slot 0 is empty.
            [TestItem.AddressC] = new()
            {
                Code = Bytes.FromHexString("0x60005415600857005b600080fd"),
                State = field == "state" ? storage : null,
                StateDiff = field == "stateDiff" ? storage : null
            }
        };

        // The trace options path may stream, so it is read back as JSON; a bundle with overrides is buffered.
        bool? failed;
        if (inOptions)
        {
            JArray result = await RunTraceCallManyAsJson(ctx, [bundle], new GethTraceOptions { StateOverrides = overrides });
            failed = result.Single().Single()["failed"]?.Value<bool>();
        }
        else
        {
            bundle.StateOverrides = overrides;
            failed = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest).Data.Single().Single().Failed;
        }

        Assert.That(failed, Is.EqualTo(field is null));
    }

    // Bundle 1 funds it and bundle 2 spends from it, which succeeds only when the bundles run cumulatively.
    private static readonly Address CallManyRelay = new("0x00000000000000000000000000000000000ca11e");

    private static IEnumerable<TestCaseData> OptionsAndBundleOverridesCases()
    {
        const string mixed = "options.stateOverrides and options.blockOverrides cannot be combined with bundle blockOverride or stateOverrides";
        static Dictionary<UInt256, Hash256> Storage() => new() { [UInt256.Zero] = Keccak.OfAnEmptyString };
        static Dictionary<Address, AccountOverride> Options() => new() { [TestItem.AddressC] = new() { StateDiff = Storage() } };
        static TransactionBundle Second(BlockOverride? blockOverride = null, Dictionary<Address, AccountOverride>? stateOverrides = null, bool empty = false) =>
            new() { Transactions = empty ? [] : [CreateTransaction(from: CallManyRelay)], BlockOverride = blockOverride, StateOverrides = stateOverrides };

        yield return new TestCaseData(Options(), Second(blockOverride: new()), mixed).SetName("{m}(second_bundle_block_override)");
        yield return new TestCaseData(Options(), Second(stateOverrides: []), mixed).SetName("{m}(second_bundle_state_overrides)");
        yield return new TestCaseData(Options(), Second(blockOverride: new(), empty: true), mixed).SetName("{m}(empty_bundle_block_override)");
        yield return new TestCaseData(new Dictionary<Address, AccountOverride> { [TestItem.AddressC] = null! }, Second(blockOverride: new()), mixed).SetName("{m}(null_account_in_options)");
        yield return new TestCaseData(new Dictionary<Address, AccountOverride>(), Second(blockOverride: new()), null).SetName("{m}(empty_options_run_the_simulation)");
        yield return new TestCaseData(Options(), Second(stateOverrides: new() { [TestItem.AddressB] = new() { State = [], StateDiff = [] } }),
            $"account {TestItem.AddressB.ToString(withEip55Checksum: true)} has both 'state' and 'stateDiff'").SetName("{m}(bundle_storage_conflict_comes_first)");
        yield return new TestCaseData(new Dictionary<Address, AccountOverride> { [TestItem.AddressC] = new() { State = Storage(), StateDiff = Storage() } },
            Second(blockOverride: new()), $"account {TestItem.AddressC.ToString(withEip55Checksum: true)} has both 'state' and 'stateDiff'").SetName("{m}(options_storage_conflict_comes_first)");
        yield return new TestCaseData(new Dictionary<Address, AccountOverride> { [TestItem.AddressC] = new() { State = Storage() } },
            Second(stateOverrides: new() { [TestItem.AddressC] = new() { StateDiff = Storage() } }), mixed).SetName("{m}(options_state_and_bundle_stateDiff_get_the_combination_error)");

        (string Field, BlockOverride Override)[] blockOverrides =
        [
            ("number", new() { Number = 1 }), ("prevRandao", new() { PrevRandao = Keccak.Zero }), ("time", new() { Time = 1 }),
            ("gasLimit", new() { GasLimit = 1 }), ("feeRecipient", new() { FeeRecipient = TestItem.AddressA }),
            ("baseFeePerGas", new() { BaseFeePerGas = UInt256.One }), ("blobBaseFee", new() { BlobBaseFee = UInt256.One }),
        ];
        foreach ((string field, BlockOverride blockOverride) in blockOverrides)
        {
            yield return new TestCaseData(new Dictionary<Address, AccountOverride>(), Second(stateOverrides: []), mixed, blockOverride).SetName($"{{m}}(options_block_override_{field})");
        }

        // As in debug_traceCall, both paths reject these fields up front.
        (string Field, BlockOverride Override, string Error)[] unsupported =
        [
            ("beaconRoot", new() { BeaconRoot = Keccak.Zero }, BlockOverride.BeaconRootNotSupported),
            ("empty_withdrawals", new() { Withdrawals = [] }, BlockOverride.WithdrawalsNotSupported),
            ("withdrawals", new() { Withdrawals = [new Withdrawal { Address = TestItem.AddressC, AmountInGwei = 1 }] }, BlockOverride.WithdrawalsNotSupported),
        ];
        foreach ((string field, BlockOverride blockOverride, string error) in unsupported)
        {
            yield return new TestCaseData(new Dictionary<Address, AccountOverride>(), Second(stateOverrides: []), error, blockOverride).SetName($"{{m}}(options_block_override_{field})");
            yield return new TestCaseData(new Dictionary<Address, AccountOverride>(), Second(), error, blockOverride).SetName($"{{m}}(options_block_override_{field}_without_bundle_overrides)");
        }

        yield return new TestCaseData(new Dictionary<Address, AccountOverride> { [TestItem.AddressC] = new() { State = Storage(), StateDiff = Storage() } }, Second(),
            BlockOverride.BeaconRootNotSupported, unsupported[0].Override).SetName("{m}(options_block_override_beaconRoot_comes_before_an_options_storage_conflict)");

        yield return new TestCaseData(new Dictionary<Address, AccountOverride>(), Second(stateOverrides: []), null, new BlockOverride()).SetName("{m}(empty_options_block_override_runs_the_simulation)");
    }

    [TestCaseSource(nameof(OptionsAndBundleOverridesCases))]
    public async Task Debug_traceCallMany_combines_options_and_bundle_overrides(Dictionary<Address, AccountOverride> optionsStateOverrides, TransactionBundle secondBundle, string? expectedError, BlockOverride? optionsBlockOverride = null)
    {
        using Context ctx = await CreateContext();

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany(
            [CreateBundle(CreateTransaction(to: CallManyRelay)), secondBundle], BlockParameter.Latest, new GethTraceOptions { StateOverrides = optionsStateOverrides, BlockOverrides = optionsBlockOverride });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(expectedError is null ? 0 : ErrorCodes.InvalidInput));
            Assert.That(result.Result.Error, Is.EqualTo(expectedError));
            Assert.That(result.Data?.Select(static r => r.Select(static t => t.Failed).ToArray()).ToArray(), Is.EqualTo(expectedError is null ? new[] { new[] { false }, new[] { false } } : null));
        }
    }

    [Test]
    public async Task Debug_trace_calls_reject_a_withdrawals_block_override([Values] bool many)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        object call = new { from = TestItem.AddressA.ToString(), to = TestItem.AddressB.ToString(), gas = "0x5208" };
        object withdrawals = new { withdrawals = new[] { new { index = "0x0", validatorIndex = "0x0", address = TestItem.AddressC.ToString(), amount = "0x1" } } };

        string response = many
            ? await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", new[] { new { transactions = new[] { call }, blockOverride = withdrawals } }, "latest")
            : await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", call, "latest", new { blockOverrides = withdrawals });

        JToken error = JToken.Parse(response)["error"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error["code"]!.Value<int>(), Is.EqualTo(many ? ErrorCodes.InvalidParams : ErrorCodes.InvalidInput));
            Assert.That(error["message"]!.Value<string>(), Is.EqualTo(BlockOverride.WithdrawalsNotSupported));
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_invalid_simulation_override_returns_original_error_message()
    {
        using Context ctx = await CreateContext();
        Address ecrecoverAddress = new("0x0000000000000000000000000000000000000001");
        TransactionBundle bundle = CreateBundle(CreateTransaction());
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [ecrecoverAddress] = new() { MovePrecompileToAddress = ecrecoverAddress }
        };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.MovePrecompileSelfReference));
            Assert.That(result.Result.Error, Is.EqualTo("MovePrecompileToAddress referenced itself in replacement"));
        }
    }

    [Test]
    public async Task Debug_traceCallMany_block_override_gaslimit_applies()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: 25_000_000));
        bundle.BlockOverride = new BlockOverride { GasLimit = 50_000_000 };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        Assert.That(result.Data.First().First().Failed, Is.False);
    }

    [Test]
    public async Task Debug_traceCallMany_mixed_bundles_preserves_order()
    {
        using Context ctx = await CreateContext();
        TransactionBundle simple = CreateBundle(CreateTransaction(gas: 4_000_000));
        TransactionBundle withOverride = CreateBundle(CreateTransaction(gas: 25_000_000));
        withOverride.BlockOverride = new BlockOverride { GasLimit = 30_000_000 };
        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([simple, withOverride], BlockParameter.Latest);

        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1, 1]));
    }

    [TestCase(3, TestName = "Debug_traceCallMany_with_minimum_block_number_gap_returns_one_entry_per_bundle")]
    [TestCase(5, TestName = "Debug_traceCallMany_with_block_number_gap_returns_one_entry_per_bundle")]
    public async Task Debug_traceCallMany_with_block_number_gap_returns_one_entry_per_bundle(int secondBundleOffset)
    {
        using Context ctx = await CreateContext();
        ulong headNumber = ctx.Blockchain.BlockTree.Head!.Number;

        TransactionBundle first = CreateBundle(CreateTransaction());
        first.BlockOverride = new BlockOverride { Number = headNumber + 1 };

        TransactionBundle second = CreateBundle(CreateTransaction(to: TestItem.AddressD));
        second.BlockOverride = new BlockOverride { Number = headNumber + (ulong)secondBundleOffset };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            ctx.DebugRpcModule.debug_traceCallMany([first, second], BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(System.Linq.Enumerable.Count(result.Data), Is.EqualTo(2));
            Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1, 1]));
        }
    }

    [Test]
    public async Task Debug_traceCallMany_caps_gas_to_gas_cap()
    {
        using Context ctx = await CreateContext();
        ulong gasCap = 50_000;
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        // Deploy contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
        // Returns gas available at start of execution as a 32-byte uint256
        byte[] runtimeCode = Bytes.FromHexString("5a60005260206000f3");
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;

        ulong nonce = ctx.Blockchain.StateReader.GetNonce(ctx.Blockchain.BlockTree.Head!.Header, TestItem.AddressD);
        Address contractAddress = ContractAddress.From(TestItem.AddressD, nonce);

        Transaction deployTx = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyD)
            .WithNonce(nonce)
            .WithCode(initCode)
            .WithGasLimit(100_000)
            .TestObject;
        await ctx.Blockchain.AddBlock(deployTx);

        TransactionBundle bundle = CreateBundle(CreateTransaction(to: contractAddress, value: 0, gas: 100_000));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        byte[] returnValue = Bytes.FromHexString((string)result[0][0]!["returnValue"]!);
        ulong gasAvailable = (ulong)returnValue.ToUInt256();
        Assert.That(gasAvailable, Is.LessThan(gasCap));
        Assert.That(gasAvailable, Is.GreaterThan(0UL));
    }

    [Test]
    public async Task Debug_traceCallMany_enforces_eip8037_total_cap(
        [Values] bool stream,
        [Values(0UL, 1_000_000_000_000UL)] ulong gasCap,
        [Values(null, Eip8037Constants.TxMaxTotalGasLimit + 1)] ulong? requestGas)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Amsterdam.Instance) { AllowTestChainOverride = false });
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.EnableTracingStreamMode = stream;
        config.GasCap = gasCap;
        TransactionBundle bundle = CreateBundle(new LegacyTransactionForRpc
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            Gas = requestGas,
            GasPrice = UInt256.Zero
        });

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That((bool)result[0][0]!["failed"]!, Is.EqualTo(requestGas is not null));
    }

    [Test]
    public async Task Debug_traceCallMany_resolves_calls_against_the_options_block_override([Values] bool stream, [Values] bool dynamicFees)
    {
        using Context ctx = await Context.Create(new CustomSpecProvider(((ForkActivation)0, Berlin.Instance), ((ForkActivation)1, London.Instance)));
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = stream;
        TransactionForRpc call = dynamicFees
            ? new EIP1559TransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Gas = GasCostOf.Transaction, MaxFeePerGas = UInt256.Zero }
            : new LegacyTransactionForRpc { From = TestItem.AddressA, To = TestItem.AddressB, Gas = GasCostOf.Transaction };
        GethTraceOptions options = new() { BlockOverrides = new BlockOverride { Number = 0 } };

        JArray result = await RunTraceCallManyAsJson(ctx, [CreateBundle(call)], options);

        // Overridden to a Berlin block, a call without fee fields still runs, and one with dynamic fees is rejected.
        Assert.That((bool)result[0][0]!["failed"]!, Is.EqualTo(dynamicFees), result.ToString());
    }

    [TestCaseSource(nameof(DebugTraceCallManyMissingGasCases))]
    public async Task Debug_traceCallMany_missing_or_zero_gas_respects_gas_cap(ulong? requestGas, ulong? configuredGasCap, bool uncapped)
    {
        using Context ctx = await CreateContext();

        ulong blockGasLimit = ctx.Blockchain.BlockTree.Head!.Header.GasLimit;
        ulong gasCap = configuredGasCap ?? blockGasLimit * 10;
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().GasCap = gasCap;

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany(
            [CreateGasProbeBundle(requestGas)],
            BlockParameter.Latest);

        GethLikeTxTrace trace = result.Data.First().First();
        UInt256 gasAvailable = trace.ReturnValue.ToUInt256();
        if (uncapped)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(trace.Failed, Is.False, "GasCap=0 should leave simulate execution uncapped rather than forcing zero gas");
                Assert.That(gasAvailable, Is.GreaterThan(UInt256.Zero));
            }
        }
        else
        {
            Assert.That(gasAvailable, Is.GreaterThan((UInt256)blockGasLimit), $"gas available should reflect gasCap ({gasCap}), not block gas limit ({blockGasLimit})");
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_overrides_enforces_eip8037_total_cap(
        [Values(0UL, 1_000_000_000_000UL)] ulong gasCap)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Amsterdam.Instance) { AllowTestChainOverride = false });
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().GasCap = gasCap;

        // State overrides route the request through the simulate path, which takes the gas-less default at face value.
        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            ctx.DebugRpcModule.debug_traceCallMany([CreateGasProbeBundle()], BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.Zero, result.Result.Error);
        GethLikeTxTrace trace = result.Data.First().First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Failed, Is.False);
            Assert.That(trace.ReturnValue.ToUInt256(), Is.GreaterThan(UInt256.Zero));
            Assert.That(trace.ReturnValue.ToUInt256(), Is.LessThanOrEqualTo((UInt256)Eip8037Constants.TxMaxTotalGasLimit));
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_overrides_caps_default_gas_by_the_executing_block_spec(
        [Values(1UL, 2UL)] ulong blockGap)
    {
        const ulong amsterdamTimestamp = 2_000_000_000;
        CustomSpecProvider specProvider = new(
            ((ForkActivation)0, Osaka.Instance),
            (ForkActivation.TimestampOnly(amsterdamTimestamp), Amsterdam.Instance));
        using Context ctx = await Context.Create(specProvider);
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().GasCap = 1_000_000_000_000UL;
        ulong secondsPerSlot = ctx.Blockchain.Container.Resolve<IBlocksConfig>().SecondsPerSlot;
        ulong headNumber = ctx.Blockchain.BlockTree.Head!.Number;

        // The first bundle runs pre-fork; the second lands exactly on activation through the derived clock,
        // after blockGap - 1 filler blocks.
        TransactionBundle preFork = CreateGasProbeBundle();
        preFork.BlockOverride = new BlockOverride { Time = amsterdamTimestamp - blockGap * secondsPerSlot };
        TransactionBundle activation = CreateGasProbeBundle();
        activation.BlockOverride = new BlockOverride { Number = headNumber + 1 + blockGap };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            ctx.DebugRpcModule.debug_traceCallMany([preFork, activation], BlockParameter.Latest);

        Assert.That(result.ErrorCode, Is.Zero, result.Result.Error);
        GethLikeTxTrace[] traces = [.. result.Data.Select(static bundle => bundle.Single())];
        Assert.That(traces, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Select(static trace => trace.Failed), Is.All.False);
            Assert.That(traces[0].ReturnValue.ToUInt256(), Is.GreaterThan((UInt256)Eip8037Constants.TxMaxTotalGasLimit),
                "pre-fork bundle must be clamped by neither EIP-7825's cap, which the processor never enforces, nor EIP-8037's, which is not active yet");
        }
    }
}
