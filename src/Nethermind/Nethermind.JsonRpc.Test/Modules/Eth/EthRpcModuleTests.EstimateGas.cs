// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Rlp;
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
    private static FrameTransactionForRpc FrameGasRequest() => new()
    {
        From = TestItem.AddressC,
        To = TestItem.AddressC,
        MaxFeePerGas = 0,
        MaxPriorityFeePerGas = 0,
        Frames =
        [
            new FrameForRpc { Mode = (byte)FrameMode.Verify, Flags = (byte)FrameFlags.ApproveExecutionAndPayment },
            new FrameForRpc { Mode = (byte)FrameMode.Sender, Target = new Address("0x000000000000000000000000000000000000dead"), Value = 1 },
        ],
        Signatures = [new FrameSignatureForRpc { Scheme = TxFrameSignature.SchemeSecp256k1 }],
    };

    private static FrameTransactionForRpc UnsignedFrameRequest() => new()
    {
        From = TestItem.AddressC,
        To = TestItem.AddressC,
        MaxFeePerGas = 0,
        MaxPriorityFeePerGas = 0,
        Frames =
        [
            new FrameForRpc { Mode = (byte)FrameMode.Verify, Flags = (byte)FrameFlags.ApproveExecutionAndPayment, ExecutionGasLimit = 50_000 },
            new FrameForRpc { Mode = (byte)FrameMode.Sender, Target = TestItem.AddressB, ExecutionGasLimit = 50_000 },
        ],
        Signatures = [new FrameSignatureForRpc { Scheme = TxFrameSignature.SchemeSecp256k1 }],
    };

    [Test]
    public async Task FrameGas_CallMethods_FillOmittedDimensions(
        [Values("eth_call", "eth_createAccessList")] string method,
        [Values(0, 1, 2)] int explicitDimension)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        if (method == "eth_createAccessList") request.MaxFeePerGas = 1_000_000_000;
        foreach (FrameForRpc frame in request.Frames!)
        {
            if (explicitDimension == 1) frame.ExecutionGasLimit = 50_000;
            if (explicitDimension == 2) frame.StateGasLimit = 200_000;
        }

        string response = await ctx.Test.TestEthRpc(method, request, "latest");

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, response);
        if (method == "eth_call") Assert.That(parsed["result"]!.Value<string>(), Is.EqualTo("0x"));
        else Assert.That(parsed["result"]!["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_FillTransaction_FillsBothDimensions([Values] bool explicitExecution)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        if (explicitExecution) request.Frames![1].ExecutionGasLimit = 50_000;

        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", request);

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, response);
        JToken frames = parsed["result"]!["tx"]!["frames"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames[0]!["executionGasLimit"]!.Value<string>(), Is.EqualTo("0x64"));
            Assert.That(frames[0]!["stateGasLimit"]!.Value<string>(), Is.EqualTo("0x0"));
            Assert.That(frames[1]!["executionGasLimit"]!.Value<string>(), Is.EqualTo(explicitExecution ? "0xc350" : "0xbb8"));
            Assert.That(frames[1]!["stateGasLimit"]!.Value<string>(), Is.EqualTo("0x2cd30"));
            Assert.That(request.Frames![0].ExecutionGasLimit, Is.Null, "RPC serialization must not mutate the caller's request");
        }

        FrameTransactionForRpc filled = FrameGasRequest();
        for (int i = 0; i < filled.Frames!.Length; i++)
        {
            filled.Frames[i].ExecutionGasLimit = Convert.ToUInt64(frames[i]!["executionGasLimit"]!.Value<string>(), 16);
            filled.Frames[i].StateGasLimit = Convert.ToUInt64(frames[i]!["stateGasLimit"]!.Value<string>(), 16);
        }
        string estimated = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest");
        string explicitEstimate = await ctx.Test.TestEthRpc("eth_estimateGas", filled, "latest");
        Assert.That(JToken.Parse(estimated)["result"]!.Value<string>(), Is.EqualTo(JToken.Parse(explicitEstimate)["result"]!.Value<string>()), estimated);
    }

    [Test]
    public async Task FrameGas_ExplicitZeroGas_IsNotFilled([Values("eth_fillTransaction", "eth_call", "eth_createAccessList")] string method, [Values] bool stateGas)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        if (method == "eth_createAccessList") request.MaxFeePerGas = 1_000_000_000;
        request.Frames![1].ExecutionGasLimit = stateGas ? 50_000UL : 0;
        request.Frames[1].StateGasLimit = stateGas ? 0 : 200_000UL;

        string response = await ctx.Test.TestEthRpc(method, request);

        Assert.That(JToken.Parse(response)["error"]!["message"]!.Value<string>(), Does.Contain("frame 1 failed: OutOfGas"));
    }

    [TestCase("eth_call", true)]
    [TestCase("eth_estimateGas", true)]
    [TestCase("eth_createAccessList", true)]
    [TestCase("eth_call", false)]
    [TestCase("eth_estimateGas", false)]
    public async Task FrameGas_RejectsLikeExplicitLimits(string method, bool missingState)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance), useFlatDb: false);
        object? blockOverride = null;
        if (missingState)
        {
            ctx.Test.WorldStateManager.FlushCache(System.Threading.CancellationToken.None);
            ctx.Test.StateDb.Clear();
        }
        else blockOverride = JsonSerializer.Deserialize<object>($$"""{"gasLimit":"0x{{ctx.Test.RpcConfig.GasCap!.Value + 1:x}}"}""");
        FrameTransactionForRpc omitted = FrameGasRequest();
        FrameTransactionForRpc explicitLimits = FrameGasRequest();
        foreach (FrameForRpc frame in explicitLimits.Frames!) (frame.ExecutionGasLimit, frame.StateGasLimit) = (50_000UL, 200_000UL);
        if (method == "eth_createAccessList") omitted.MaxFeePerGas = explicitLimits.MaxFeePerGas = 1_000_000_000;

        string expected = await ctx.Test.TestEthRpc(method, missingState ? [explicitLimits, "latest"] : [explicitLimits, "latest", null, blockOverride]);
        string response = await ctx.Test.TestEthRpc(method, missingState ? [omitted, "latest"] : [omitted, "latest", null, blockOverride]);

        Assert.That(JToken.Parse(expected)["error"]!["code"]!.Value<int>(), Is.EqualTo(missingState ? ErrorCodes.ResourceUnavailable : ErrorCodes.InvalidInput), expected);
        Assert.That(response, Is.EqualTo(expected));
    }

    [Test]
    public async Task FrameGas_UsesEarlierFrameWrites([Values("eth_estimateGas", "eth_call", "eth_createAccessList")] string method, [Values] bool atomic, [Values] bool catchesInnerFailure)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        if (method == "eth_createAccessList") request.MaxFeePerGas = 1_000_000_000;
        Address contract = request.Frames![1].Target!;
        request.Frames[1].Value = 0;
        request.Frames[1].Flags = atomic ? (byte)FrameFlags.AtomicBatch : (byte)0;
        request.Frames = [request.Frames[0], request.Frames[1], new FrameForRpc { Mode = (byte)FrameMode.Sender, Target = contract, Data = new byte[] { 1 } }];
        // The first call requires zero storage and writes one; the second requires one. Each probe must restore state before replaying the batch.
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{contract}}}":{"code":"0x366013575f5415600d575f5ffd5b60015f55005b5f54600114601f575f5ffd5b00"}}""")!;

        if (catchesInnerFailure)
        {
            Address wrapper = TestItem.AddressD;
            request.Frames[1].Target = wrapper;
            byte[] code = Prepare.EvmCode.Call(contract, 1_000_000).Op(Instruction.POP).Op(Instruction.STOP).Done;
            string wrapperCode = Convert.ToHexString(code);
            overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{wrapper}}}":{"code":"0x{{{wrapperCode}}}"},"{{{contract}}}":{"code":"0x366013575f5415600d575f5ffd5b60015f55005b5f54600114601f575f5ffd5b00"}}""")!;
        }

        string response = await ctx.Test.TestEthRpc(method, request, "latest", overrides);

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, response);
        if (method == "eth_estimateGas") Assert.That(parsed["result"]!.Value<string>(), Is.Not.EqualTo("0x0"));
        if (method == "eth_createAccessList") Assert.That(parsed["result"]!["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_FillTransaction_MaximumFrameCount()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        FrameForRpc verify = request.Frames![0];
        request.Frames = new FrameForRpc[Eip8141Constants.MaxFrames];
        request.Frames[0] = verify;
        for (int i = 1; i < request.Frames.Length; i++)
            request.Frames[i] = new FrameForRpc { Mode = (byte)FrameMode.Sender, Target = TestItem.AddressB };

        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", request);

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, response);
        Assert.That((JArray)parsed["result"]!["tx"]!["frames"]!, Has.Count.EqualTo(Eip8141Constants.MaxFrames));
    }

    [Test]
    public async Task FrameGas_EstimateGas_FillsEveryFrameWhenProbeBudgetRunsOut([Values] bool lastFrameNeedsHeadroom)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        ctx.Test.RpcConfig.EstimateErrorMargin = 0;
        FrameTransactionForRpc request = FrameGasRequest();
        Address loop = request.Frames![1].Target!;
        FrameForRpc verify = request.Frames[0];
        request.Frames = new FrameForRpc[Eip8141Constants.MaxFrames];
        request.Frames[0] = verify;
        for (int i = 1; i < request.Frames.Length; i++)
            request.Frames[i] = new FrameForRpc { Mode = (byte)FrameMode.Sender, Target = loop };
        if (lastFrameNeedsHeadroom) request.Frames[^1].Target = TestItem.AddressD;
        // A 512-iteration loop: an exact search on every frame needs more probes than the estimator allows. The
        // headroom check reverts unless 50,000 gas remains: within an even split, so it is measured as succeeding, but
        // far above what it uses, so an unverified limit fails.
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{loop}}}":{"code":"0x6102005b600190038060035700"},"{{{TestItem.AddressD}}}":{"code":"0x5a6200c35010600c575f5ffd5b00"}}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides);

        JToken? error = JToken.Parse(response)["error"];
        if (lastFrameNeedsHeadroom) Assert.That(error?["message"]?.Value<string>(), Does.Contain("probes"), response);
        else Assert.That(error, Is.Null, response);
    }

    [Test]
    public async Task FrameGas_EstimateGas_BlockNumberOverrideUsesBaseState([Values] bool useFlatDb)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance), useFlatDb: useFlatDb);
        object blockOverride = JsonSerializer.Deserialize<object>("""{"number":"0x100"}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", FrameGasRequest(), "latest", null, blockOverride);

        Assert.That(JToken.Parse(response)["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_EstimateGas_RunsInTheRequestedBlock()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        // Reverts unless NUMBER is the requested block, as it is for the gas estimate itself.
        string number = ctx.Test.BlockTree.Head!.Number.ToString("x16");
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{request.Frames![1].Target}}}":{"code":"0x4367{{{number}}}146011575f5ffd5b00"}}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides);

        Assert.That(JToken.Parse(response)["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_EstimateFrameGas_ReportsMissingState([Values] bool withOverride)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        BlockHeader pruned = Build.A.BlockHeader.WithNumber(1_000).WithStateRoot(TestItem.KeccakA).TestObject;
        Transaction tx = FrameGasRequest().ToTransaction().Data!;
        bool[] fill = [true, true];
        Dictionary<Address, AccountOverride>? stateOverride = withOverride ? new() { [TestItem.AddressB] = new AccountOverride { Balance = 1 } } : null;

        Result<TxFrame[]> result = ctx.Test.Bridge.EstimateFrameGas(pruned, tx, fill, fill, 1_000_000, 150, stateOverride, null, default, out _);

        Assert.That(result.Error, Does.StartWith("No state available"));
    }

    [Test]
    public async Task FrameGas_EstimateGas_FillsLaterFrameThatRevertsWithoutHeadroom()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        request.Frames = [.. request.Frames!, new FrameForRpc { Mode = (byte)FrameMode.Sender, Target = TestItem.AddressD }];
        // Reverts unless 2,000,000 gas remains, more than the frame's share of an even split.
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{TestItem.AddressD}}}":{"code":"0x5a621e848010600c575f5ffd5b00"}}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides);

        Assert.That(JToken.Parse(response)["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_EstimateGas_ReportsVerifierRevert()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{request.From}}}":{"code":"0x5f5ffd"}}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides);

        Assert.That(JToken.Parse(response)["error"]!["message"]!.Value<string>(), Does.Contain("VERIFY frame reverted"));
    }

    [TestCase("0x5f5ffd", "Revert", ErrorCodes.ExecutionReverted)]
    [TestCase("0x5b5f56", "OutOfGas", ErrorCodes.InvalidInput)]
    public async Task FrameGas_EstimateGas_ReportsFrameThatFailsAtEveryBudget(string code, string error, int errorCode)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{request.Frames![1].Target}}}":{"code":"{{{code}}}"}}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides);

        JToken rpcError = JToken.Parse(response)["error"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpcError["message"]!.Value<string>(), Does.Contain($"frame 1 failed: {error}"));
            Assert.That(rpcError["code"]!.Value<int>(), Is.EqualTo(errorCode));
        }
    }

    [Test]
    public async Task FrameGas_EstimateGas_ChecksFinalAffordability([Values] bool sufficient)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        request.MaxFeePerGas = 1;
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{request.From}}}":{"balance":"{{{(sufficient ? "0x3d090" : "0x1")}}}"}}""")!;
        object blockOverride = JsonSerializer.Deserialize<object>("""{"baseFeePerGas":"0x0"}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides, blockOverride);

        JToken parsed = JToken.Parse(response);
        if (sufficient) Assert.That(parsed["error"], Is.Null, response);
        else Assert.That(parsed["error"]!["message"]!.Value<string>(), Does.Contain("VERIFY frame reverted"));
    }

    [Test]
    public async Task FrameGas_EstimateGas_RunsFramesAtTheRequestedGasPrice([Values] bool explicitLimits)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        request.MaxFeePerGas = 2;
        request.MaxPriorityFeePerGas = 1;
        if (explicitLimits)
        {
            (request.Frames![0].ExecutionGasLimit, request.Frames[0].StateGasLimit) = (50_000, 0);
            (request.Frames[1].ExecutionGasLimit, request.Frames[1].StateGasLimit) = (50_000, 200_000);
        }
        // The second frame reverts unless GASPRICE is positive.
        object overrides = JsonSerializer.Deserialize<object>($$$"""{"{{{request.From}}}":{"balance":"0xde0b6b3a7640000"},"{{{request.Frames![1].Target}}}":{"code":"0x3a15600657005b5f5ffd"}}""")!;
        object blockOverride = JsonSerializer.Deserialize<object>("""{"baseFeePerGas":"0x0"}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest", overrides, blockOverride);

        Assert.That(JToken.Parse(response)["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_EstimateGas_RespectsRpcGasCap([Values] bool tooSmall)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        ctx.Test.RpcConfig.GasCap = tooSmall ? 190_000UL : 1_000_000UL;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", FrameGasRequest(), "latest");

        JToken parsed = JToken.Parse(response);
        if (tooSmall) Assert.That(parsed["error"]!["message"]!.Value<string>(), Does.Contain("gas limit"));
        else Assert.That(parsed["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameGas_EstimateGas_RejectsExplicitExecutionAboveTransactionCap()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc request = FrameGasRequest();
        request.Frames![1].ExecutionGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 1;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", request, "latest");

        Assert.That(JToken.Parse(response)["error"]!["message"]!.Value<string>(), Does.Contain("gas limit"));
    }

    [Test]
    public async Task FrameGas_EstimateGas_RespectsBlockGasLimit([Values] bool tooSmall)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        object blockOverride = JsonSerializer.Deserialize<object>(tooSmall ? """{"gasLimit":"0x100"}""" : """{"gasLimit":"0x30d40"}""")!;

        string response = await ctx.Test.TestEthRpc("eth_estimateGas", FrameGasRequest(), "latest", null, blockOverride);

        JToken parsed = JToken.Parse(response);
        if (tooSmall) Assert.That(parsed["error"]!["message"]!.Value<string>(), Does.Contain("gas limit"));
        else Assert.That(parsed["error"], Is.Null, response);
    }

    [Test]
    public async Task FrameRpc_UnsignedTransaction_Succeeds(
        [Values("eth_call", "eth_estimateGas", "eth_fillTransaction", "eth_simulateV1")] string method)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc transaction = UnsignedFrameRequest();

        object request = method == "eth_simulateV1"
            ? new { blockStateCalls = new[] { new { calls = new[] { transaction } } }, validation = false }
            : transaction;
        string response = await ctx.Test.TestEthRpc(method, request);

        JToken parsed = JToken.Parse(response);
        Assert.That(parsed["error"], Is.Null, response);
        Assert.That(parsed["result"], Is.Not.Null);
        if (method == "eth_call") Assert.That(parsed["result"]!.Value<string>(), Is.EqualTo("0x"));
        if (method == "eth_simulateV1") Assert.That(parsed["result"]![0]!["calls"]![0]!["status"]!.Value<string>(), Is.EqualTo("0x1"));
        if (method == "eth_fillTransaction")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((JArray)parsed["result"]!["tx"]!["frames"]!, Has.Count.EqualTo(2));
                Assert.That((JArray)parsed["result"]!["tx"]!["signatures"]!, Has.Count.EqualTo(1));
            }
        }
    }

    [Test]
    public async Task FrameRpc_EstimateGas_PlaceholderCoversSignedTransaction()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        FrameTransactionForRpc placeholder = UnsignedFrameRequest();
        FrameTransactionForRpc signed = UnsignedFrameRequest();
        // Explicit limits: filling would change the frames the signature commits to.
        foreach (FrameForRpc frame in placeholder.Frames!) frame.StateGasLimit = 0;
        foreach (FrameForRpc frame in signed.Frames!) frame.StateGasLimit = 0;
        signed.Nonce = ctx.Test.ReadOnlyState.GetNonce(TestItem.AddressC);
        Transaction tx = signed.ToTransaction().Data!;
        tx.ChainId = ctx.Test.Bridge.GetChainId();
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = new Ecdsa().Sign(TestItem.PrivateKeyC, in sigHash);
        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        signed.Signatures![0].Signature = vrs;

        string placeholderEstimate = await ctx.Test.TestEthRpc("eth_estimateGas", placeholder);
        string signedEstimate = await ctx.Test.TestEthRpc("eth_estimateGas", signed);

        Assert.That(JToken.Parse(signedEstimate)["error"], Is.Null, signedEstimate);
        Assert.That(Convert.ToUInt64(JToken.Parse(placeholderEstimate)["result"]!.Value<string>(), 16),
            Is.GreaterThanOrEqualTo(Convert.ToUInt64(JToken.Parse(signedEstimate)["result"]!.Value<string>(), 16)), placeholderEstimate);
    }

    [Test]
    public async Task Eth_estimateGas_web3_should_return_insufficient_balance_error()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, TestAccount);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestAccountAddress}\",\"gasPrice\":\"0x100000\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\", \"value\": 500}}")!;
        string serialized =
            await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"insufficient funds for transfer\"},\"id\":67}"));
        AssertAccountDoesNotExist(ctx, TestAccount);
    }


    [Test]
    public async Task Eth_estimateGas_web3_sample_not_enough_gas_system_account()
    {
        using Context ctx = await Context.Create();
        AssertAccountDoesNotExist(ctx, Address.SystemUser);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}")!;
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
            $"{{\"from\":\"{TestAccountAddress}\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}")!;
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
            $"{{\"from\":\"{TestAccountAddress}\",\"gas\":\"0x100000\", \"data\": \"{BalanceOfCallData}\", \"to\": \"{BatTokenAddress}\"}}")!;
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
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}")!;
        string serializedCreateAccessList = await test.TestEthRpc("eth_createAccessList",
            transaction, "0x0", null, optimize.ToString().ToLower());

        transaction.AccessList = test.JsonSerializer.Deserialize<AccessListForRpc>(JToken.Parse(serializedCreateAccessList).SelectToken("result.accessList")!.ToString())!;
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
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithConfig(new JsonRpcConfig() { EstimateErrorMargin = 0, Timeout = -1 })
            .Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListForRpc accessList) = GetTestAccessList(2, senderAccessList);

        AccessListTransactionForRpc transaction =
            test.JsonSerializer.Deserialize<AccessListTransactionForRpc>(
                $"{{\"type\":\"0x1\", \"from\": \"{Address.SystemUser}\", \"data\": \"{code.ToHexString(true)}\"}}")!;
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
                $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}")!;
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
            $"{{\"from\": \"{BatTokenAddress}\", \"to\": \"{BatTokenAddress}\"}}")!;
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_gas_pricing()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x10\"}}")!;
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing_after_1559_legacy()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x100000000\"}}")!;
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_without_gas_pricing_after_1559_new_type_of_transaction()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"to\": \"{SecondaryTestAddress}\", \"type\": \"0x2\"}}")!;
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_with_base_fee_opcode()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string dataStr = BaseFeeReturnCode.ToHexString(true);
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"type\": \"0x2\", \"data\": \"{dataStr}\"}}")!;
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xe891\",\"id\":67}"));
    }

    [Test]
    public async Task Estimate_gas_feeless_with_positive_blockOverride_baseFeePerGas_uses_zero_base_fee()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        const string revertOnNonZeroBaseFee = "0x4860095760006000f35b60006000fd";
        object? transaction = JsonSerializer.Deserialize<object>(
            $"{{\"from\":\"{SecondaryTestAddress}\",\"to\":\"{SecondaryTestAddress}\",\"data\":\"{revertOnNonZeroBaseFee}\"}}");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            $"{{\"{SecondaryTestAddress}\":{{\"code\":\"{revertOnNonZeroBaseFee}\"}}}}");
        object? blockOverride = JsonSerializer.Deserialize<object>("""{"baseFeePerGas":"0x100"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride, blockOverride);

        Assert.That(JToken.Parse(serialized)["error"], Is.Null, "unpriced estimate must zero the base fee override instead of reverting");
        Assert.That(JToken.Parse(serialized)["result"], Is.Not.Null);
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
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""")!;
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
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""")!;
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
            $$"""{"from": "{{SecondaryTestAddress}}", "type": "0x2", "data": "{{dataStr}}", "gas": 1000000}""")!;
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
    public async Task Estimate_gas_executes_in_the_context_of_the_requested_block()
    {
        using Context ctx = await Context.Create();
        ulong headNumber = ctx.Test.BlockTree.Head!.Number;

        // NUMBER, PUSH4 headNumber, EQ, PUSH1 14, JUMPI, PUSH1 0, DUP1, REVERT, JUMPDEST, STOP
        string code = $"0x4363{headNumber:x8}14600e57600080fd5b00";
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"0xc200000000000000000000000000000000000000","data":"0x01"}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            $$$"""{"0xc200000000000000000000000000000000000000":{"code":"{{{code}}}"}}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        Assert.That(JToken.Parse(serialized)["error"], Is.Null, $"the contract reverts unless NUMBER is the latest block's: {serialized}");
        Assert.That(JToken.Parse(serialized)["result"], Is.Not.Null, serialized);
    }

    [TestCase("0xfe", null, "invalid opcode: INVALID", TestName = "Designated invalid opcode")]
    [TestCase("0x0c", null, "invalid opcode: opcode 0xc not defined", TestName = "Unassigned opcode")]
    [TestCase("0x01", null, "stack underflow (0 <=> 2)", TestName = "Stack underflow")]
    [TestCase("0x600056", null, "invalid jump destination", TestName = "Invalid jump destination")]
    [TestCase("0xfe", 30_000_000ul, "invalid opcode: INVALID", TestName = "Invalid opcode with the request above the per-transaction cap")]
    // GAS, PUSH4 20000000, LT, PUSH1 11, JUMPI, INVALID, JUMPDEST: invalid below 20M gas, and above it STOP.
    [TestCase("0x5a6301312d0010600b57fe5b00", 30_000_000ul, "invalid opcode: INVALID", TestName = "Invalid at the cap, succeeding at the requested gas")]
    // Same guard; above 20M gas it loops until out of gas.
    [TestCase("0x5a6301312d0010600b57fe5b5b600c56", 30_000_000ul, "invalid opcode: INVALID", TestName = "Invalid at the cap, out of gas at the requested gas")]
    public async Task Estimate_gas_execution_failure_at_the_highest_gas_limit(string code, ulong? gas, string expected)
    {
        string serialized = await EstimateGasAgainstCode(code, gas);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(expected), serialized);
    }

    [Test]
    public async Task Estimate_gas_stack_overflow_is_reported_with_the_standard_text()
    {
        string serialized = await EstimateGasAgainstCode("0x" + string.Concat(Enumerable.Repeat("5f", 1025)), gas: null);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("stack limit reached 1024 (1023)"), serialized);
    }

    [Test]
    public async Task Estimate_gas_revert_at_the_capped_gas_limit_is_reported_as_a_revert()
    {
        // GAS, PUSH4 10000000, LT, PUSH1 11, JUMPI, INVALID, JUMPDEST, PUSH1 0, DUP1, REVERT: reverts above 10M gas.
        string serialized = await EstimateGasAgainstCode("0x5a6300989680 10600b57fe5b600080fd".Replace(" ", ""), 30_000_000ul);

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","error":{"code":3,"message":"execution reverted","data":"0x"},"id":67}""")).Using(JToken.EqualityComparer),
            "the run at the EIP-7825 cap reverts");
    }

    [Test]
    public async Task Estimate_gas_below_the_base_cost_falls_back_to_the_overridden_block_gas_limit()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"0xc200000000000000000000000000000000000000","data":"0x01","gas":"0x3e8"}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xc200000000000000000000000000000000000000":{"code":"0x5b600056"}}""");
        object? blockOverride = JsonSerializer.Deserialize<object>("""{"gasLimit":"0xc350"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride, blockOverride);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("gas required exceeds allowance (50000)"), serialized);
    }

    [Test]
    public async Task Estimate_gas_invalid_use_of_an_opcode_the_spec_defines_keeps_its_report()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Eip8141Prototype.Instance));
        ulong blockGasLimit = ctx.Test.BlockTree.Head!.GasLimit;
        // PUSH1 0, PUSH1 0, PUSH1 0, APPROVE: APPROVE outside a frame transaction is a bad instruction.
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"0xc200000000000000000000000000000000000000","data":"0x01"}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xc200000000000000000000000000000000000000":{"code":"0x600060006000aa"}}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo($"failed with {blockGasLimit} gas: invalid instruction"), serialized);
    }

    private const string OneEther = "0xde0b6b3a7640000";
    private const string BelowBaseCostAtOneGwei = "0x1319718a0c00"; // 20999 gwei

    // PUSH1 1, PUSH1 0, SSTORE, STOP
    private const string StoringCode = "0x600160005500";

    [TestCase("""{"type":"0x2","maxPriorityFeePerGas":"0x3b9aca00"}""", OneEther, "tip above the zero fee cap", TestName = "Priority fee only")]
    [TestCase("""{"type":"0x2","maxPriorityFeePerGas":"0x3b9aca00"}""", BelowBaseCostAtOneGwei, "tip above the zero fee cap", TestName = "Priority fee only, balance below the base cost at the priority fee")]
    [TestCase("""{"type":"0x2","maxFeePerGas":"0x3b9aca00"}""", OneEther, "unpriced", TestName = "Fee cap only")]
    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", OneEther, "tip above the 10 wei fee cap", TestName = "Fee cap below the priority fee")]
    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", "0x3e8", "tip above the 10 wei fee cap at 100 gas", TestName = "Fee cap below the priority fee, balance funding 100 gas")]
    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", "0x0", "insufficient funds for transfer", TestName = "Fee cap below the priority fee, empty sender")]
    [TestCase("""{"gasPrice":"0x3b9aca00"}""", OneEther, "unpriced", TestName = "Legacy gas price")]
    [TestCase("""{"gasPrice":"0x3b9aca00"}""", BelowBaseCostAtOneGwei, "allowance 20999", TestName = "Legacy gas price, balance below the base cost")]
    [TestCase("""{}""", OneEther, "unpriced", TestName = "No fee fields")]
    public async Task Estimate_gas_fee_field_shapes(string feeFields, string balance, string expectation)
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        ulong blockGasLimit = ctx.Test.BlockTree.Head!.GasLimit;
        string sender = TestItem.AddressA.ToString(withEip55Checksum: true);
        JsonObject request = JsonNode.Parse(feeFields)!.AsObject();
        request["from"] = sender;
        request["to"] = "0xc200000000000000000000000000000000000000";
        object? stateOverride = JsonSerializer.Deserialize<object>(
            $$$"""{"{{{sender}}}":{"balance":"{{{balance}}}"},"0xc200000000000000000000000000000000000000":{"code":"{{{StoringCode}}}"}}""");
        object? blockOverride = JsonSerializer.Deserialize<object>("""{"baseFeePerGas":"0x7"}""");
        object? unpricedRequest = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{sender}}","to":"0xc200000000000000000000000000000000000000"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", JsonSerializer.Deserialize<object>(request.ToJsonString()), "latest", stateOverride, blockOverride);
        string unpriced = await ctx.Test.TestEthRpc("eth_estimateGas", unpricedRequest, "latest", stateOverride, blockOverride);

        string tipAboveFeeCap = $"failed with {blockGasLimit} gas: max priority fee per gas higher than max fee per gas: address {sender}, maxPriorityFeePerGas: 1000000000, maxFeePerGas: ";
        switch (expectation)
        {
            case "unpriced":
                Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(JToken.Parse(unpriced)["result"]!.Value<string>()).And.Not.Null,
                    $"a priced request the balance funds estimates as an unpriced one: {serialized}");
                break;
            case "tip above the zero fee cap":
                Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(tipAboveFeeCap + "0"),
                    $"no fee cap skips the balance cap, and the run is rejected for its priority fee: {serialized}");
                break;
            case "tip above the 10 wei fee cap":
                Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(tipAboveFeeCap + "10"),
                    $"the balance funds far more than the block gas limit at 10 wei: {serialized}");
                break;
            case "tip above the 10 wei fee cap at 100 gas":
                Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(),
                    Is.EqualTo(tipAboveFeeCap.Replace($"failed with {blockGasLimit} gas", "failed with 100 gas") + "10"),
                    $"1000 wei funds 100 gas at 10 wei, and the run there is rejected for its priority fee: {serialized}");
                break;
            case "insufficient funds for transfer":
                Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("insufficient funds for transfer"),
                    $"a priced request needs a balance above its value: {serialized}");
                break;
            default:
                Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("gas required exceeds allowance (20999)"),
                    $"20999 gwei funds 20999 gas at 1 gwei: {serialized}");
                break;
        }
    }

    private static async Task<string> EstimateGasAgainstCode(string code, ulong? gas)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        string gasField = gas is null ? "" : $",\"gas\":\"{gas.Value.ToHexString(true)}\"";
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"0xc200000000000000000000000000000000000000","data":"0x01"{{gasField}}}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            $$$"""{"0xc200000000000000000000000000000000000000":{"code":"{{{code}}}"}}""");

        return await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);
    }

    [Test]
    public async Task Estimate_gas_creation_out_of_gas_depositing_its_code_reports_the_allowance()
    {
        using Context ctx = await Context.Create();
        // PUSH2 1000, PUSH1 0, RETURN: 1000 bytes of code cost more to deposit than the 100000 gas leaves.
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","gas":"0x186a0","data":"0x6103e86000f3"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("gas required exceeds allowance (100000)"), serialized);
    }

    [TestCase(",\"data\":\"0x01\"", null, "gas required exceeds allowance (20000)", TestName = "Call data")]
    [TestCase("", "0x5208", null, TestName = "Plain transfer runs at the base cost")]
    public async Task Estimate_gas_with_a_gas_cap_below_the_base_cost(string dataField, string? expectedResult, string? expectedError)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Osaka.Instance));
        ctx.Test.RpcConfig.GasCap = 20_000;
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"0xc200000000000000000000000000000000000000"{{dataField}}}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        JToken response = JToken.Parse(serialized);
        Assert.That(response["result"]?.Value<string>(), Is.EqualTo(expectedResult), serialized);
        Assert.That(response["error"]?["message"]?.Value<string>(), Is.EqualTo(expectedError), serialized);
    }

    [Test]
    public async Task Estimate_gas_intrinsic_cost_above_the_per_transaction_cap_reports_the_allowance()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Amsterdam.Instance));
        string data = new string('1', 900_000).Insert(0, "0x");
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"0xc200000000000000000000000000000000000000","gas":"0x1c9c380","data":"{{data}}"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("gas required exceeds allowance (30000000)"), serialized);
    }

    [Test]
    public async Task Estimate_gas_uses_block_gas_limit_when_not_specified()
    {
        using Context ctx = await Context.Create();

        string blockNumberResponse = await ctx.Test.TestEthRpc("eth_blockNumber");
        string blockNumber = JToken.Parse(blockNumberResponse).Value<string>("result")!;
        string blockResponse = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockNumber, false);
        ulong blockGasLimit = Convert.ToUInt64(JToken.Parse(blockResponse).SelectToken("result.gasLimit")!.Value<string>(), 16);

        // gasCap above blockGasLimit — estimate should be bounded by blockGasLimit, not gasCap (matches Geth)
        ctx.Test.RpcConfig.GasCap = blockGasLimit + 1_000_000;

        await TestEstimateGasOutOfGas(ctx, null, blockGasLimit, $"gas required exceeds allowance ({blockGasLimit})");
    }

    [Test]
    public async Task Estimate_gas_treats_zero_gas_as_not_specified()
    {
        using Context ctx = await Context.Create();

        string blockNumberResponse = await ctx.Test.TestEthRpc("eth_blockNumber");
        string blockNumber = JToken.Parse(blockNumberResponse).Value<string>("result")!;
        string blockResponse = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockNumber, false);
        ulong blockGasLimit = Convert.ToUInt64(JToken.Parse(blockResponse).SelectToken("result.gasLimit")!.Value<string>(), 16);

        ctx.Test.RpcConfig.GasCap = blockGasLimit + 1_000_000;

        await TestEstimateGasOutOfGas(ctx, 0, blockGasLimit, $"gas required exceeds allowance ({blockGasLimit})");
    }

    [Test]
    public async Task Estimate_gas_with_explicit_zero_gas_limit_simple_transfer()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{TestItem.AddressA}\", \"to\": \"{SecondaryTestAddress}\", \"gas\": \"0x0\"}}")!;

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
            $"{{\"from\": \"{BatTokenAddress}\", \"to\": \"{SecondaryTestAddress}\"}}")!;

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
        ulong eip7976Floor100 = GasCostOf.Transaction + 100UL * Eip7976Spec.GasCosts.TxDataNonZeroMultiplier * Eip7976Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7976Spec, new byte[100], 100_000UL, null,
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7976Floor100.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7976: data heavy tx returns floor cost");

        // EIP-7623: 100 zero bytes → floor = 21000 + 100 * 10 = 22000
        ulong eip7623Floor100 = GasCostOf.Transaction + 100UL * Prague.Instance.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Prague.Instance, new byte[100], 100_000UL, null,
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7623Floor100.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7623: data heavy tx returns lower floor");

        // EIP-7976: gas at standard but below floor → "gas below floor data cost"
        // 1 zero byte: standard = 21000 + 4 = 21004; floor = 21000 + 1*4*16 = 21064
        const ulong atStandard = GasCostOf.Transaction + GasCostOf.TxDataZero;
        ulong eip7976Floor1Byte = GasCostOf.Transaction + 1UL * Eip7976Spec.GasCosts.TxDataNonZeroMultiplier * Eip7976Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7976Spec, new byte[] { 0 }, atStandard, null,
                $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32000,\"message\":\"failed with {atStandard} gas: gas below floor data cost: have {atStandard}, want {eip7976Floor1Byte}\"}},\"id\":67}}")
            .SetName("EIP-7976: gas at standard but below floor returns floor error");

        // EIP-7976: mixed calldata (0x00001122 = 2 zero + 2 nonzero bytes)
        ulong eip7976Floor4 = GasCostOf.Transaction + 4UL * Eip7976Spec.GasCosts.TxDataNonZeroMultiplier * Eip7976Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7976Spec, new byte[] { 0x00, 0x00, 0x11, 0x22 }, 100_000UL, null,
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7976Floor4.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7976: mixed calldata returns floor");

        ulong eip2780ValueTransferBase = GasCostOf.TransactionEip2780
            + Eip8038Constants.ColdAccountAccess
            + GasCostOf.TxValueCostEip2780;

        // EIP-7981: access list with 1 address, no calldata - standard wins.
        ulong eip7981Standard = eip2780ValueTransferBase + Eip8038Constants.AccessListAddressCost
            + 80UL * Eip7981Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7981Spec, Array.Empty<byte>(), 100_000UL,
                new AccessList.Builder().AddAddress(Address.Zero).Build(),
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7981Standard.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7981: standard wins with access list");

        // EIP-7976 charges every calldata byte at the floor's nonzero-token rate.
        ulong eip7981FloorWithCalldata = eip2780ValueTransferBase
            + (100UL * Eip7981Spec.GasCosts.TxDataNonZeroMultiplier + 80UL)
            * Eip7981Spec.GasCosts.TotalCostFloorPerToken;
        yield return new TestCaseData(Eip7981Spec, new byte[100], 100_000UL,
                new AccessList.Builder().AddAddress(Address.Zero).Build(),
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{eip7981FloorWithCalldata.ToHexString(true)}\",\"id\":67}}")
            .SetName("EIP-7981: floor wins with calldata and access list");
    }

    [TestCaseSource(nameof(EstimateGasFloorCostCases))]
    public async Task Eth_estimateGas_floor_cost(IReleaseSpec spec, byte[] data, ulong gasLimit, AccessList? accessList, string expectedJson)
    {
        TestSpecProvider specProvider = new(spec);
        using Context ctx = await Context.Create(specProvider);

        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(gasLimit)
            .WithData(data);
        if (accessList is not null)
            txBuilder.WithAccessList(accessList);
        Transaction tx = txBuilder.WithValue(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = null;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        Assert.That(serialized, Is.EqualTo(expectedJson));
    }

    [Test]
    public async Task Eth_estimateGas_gas_hint_above_eip8037_total_cap_returns_estimate()
    {
        // The over-cap gas hint is invalid for inclusion, but the estimator must clamp to
        // TX_MAX_TOTAL_GAS_LIMIT and still return the executable minimum, not the cap error.
        using Context ctx = await Context.Create(new TestSpecProvider(Amsterdam.Instance));

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(Eip8037Constants.TxMaxTotalGasLimit + 1)
            .WithValue(0)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = null;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        // Zero-value transfer to an existing EOA: TX_BASE_COST + COLD_ACCOUNT_ACCESS.
        ulong expected = GasCostOf.TransactionEip2780 + Eip8038Constants.ColdAccountAccess;
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected.ToHexString(true)}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_estimateGas_value_transfer_creating_account_is_within_the_error_margin()
    {
        // Production error margin (the shared Context defaults to 0). Creating the account costs more than the
        // base transaction, so the transfer is searched: the run at the block gas limit uses 204600, the
        // (204600 + 2300) * 64 / 63 = 210184 guess succeeds, and 207391 succeeds within 1.5% of the upper bound.
        using Context ctx = await Context.Create(new TestSpecProvider(Amsterdam.Instance),
            estimateErrorMargin: GasEstimator.DefaultErrorMargin);

        Assert.That(ctx.Test.ReadOnlyState.AccountExists(TestAccount), Is.False, "recipient must be a fresh account");

        Transaction tx = Build.A.Transaction
            .WithTo(TestAccount)
            .WithValue(1)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        EIP1559TransactionForRpc transaction = new(tx, new(tx.ChainId ?? BlockchainIds.Mainnet));
        transaction.GasPrice = null;
        transaction.Gas = null; // no gas cap, or Build.A.Transaction's default 21000 caps the search

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction);

        const ulong expected = 207_391;
        Assert.That(Eip8037NewAccountTransferGas, Is.EqualTo(204_600ul), "gas the transfer uses");
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected.ToHexString(true)}\",\"id\":67}}"));
    }

    private static async Task TestEstimateGasOutOfGas(Context ctx, ulong? specifiedGasLimit, ulong expectedGasLimit, string message)
    {
        string gasParam = specifiedGasLimit.HasValue ? $", \"gas\": \"0x{specifiedGasLimit.Value:X}\"" : "";
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\"{gasParam}, \"data\": \"{InfiniteLoopCode.ToHexString(true)}\"}}")!;

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
            Is.EqualTo("gas required exceeds allowance (50000)"), "the probe at the 50000 block gas limit is below the intrinsic cost");
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

    [TestCase("0x0", null, TestName = "Unpriced from an empty sender")]
    [TestCase("0x1319718a5000", "0x3b9aca00", TestName = "Priced from a sender funding exactly the base cost")]
    public async Task Eth_estimateGas_blob_transaction_without_blob_fee_cap_prices_blob_gas_at_zero(string balance, string? maxFeePerGas)
    {
        ISpecProvider specProvider = new TestSpecProvider(Cancun.Instance);
        Block[] blocks = [Build.A.Block.WithNumber(0).WithGasLimit(30_000_000).WithExcessBlobGas(1ul).TestObject];
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        using TestRpcBlockchain test = await TestRpcBlockchain
            .ForTest(SealEngineType.NethDev)
            .WithBlockFinder(blockTree)
            .Build(specProvider);

        string fees = maxFeePerGas is null ? "" : ",\"maxFeePerGas\":\"" + maxFeePerGas + "\",\"maxPriorityFeePerGas\":\"0x0\"";
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","type":"0x3"{{fees}},"blobVersionedHashes":["0x0122000000000000000000000000000000000000000000000000000000000000"]}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            $$$"""{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"{{{balance}}}","nonce":"0x0"}}""");

        string serialized = await test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":"0x5208","id":67}""")).Using(JToken.EqualityComparer),
            "no blob fee cap adds nothing to the cost and runs at a zero blob base fee");
    }

    [Test]
    public async Task Eth_estimateGas_blob_transaction_without_blob_fee_cap_names_a_failure_without_standard_text_at_the_funded_gas()
    {
        ISpecProvider specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        Block[] blocks = [Build.A.Block.WithNumber(0).WithGasLimit(30_000_000).WithExcessBlobGas(1ul).TestObject];
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        using TestRpcBlockchain test = await TestRpcBlockchain
            .ForTest(SealEngineType.NethDev)
            .WithBlockFinder(blockTree)
            .Build(specProvider);

        // APPROVE outside a frame transaction is a bad instruction without standard text; 100000 gwei funds
        // exactly 100000 gas at 1 gwei.
        object? transaction = JsonSerializer.Deserialize<object>(
            """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0xc200000000000000000000000000000000000000","type":"0x3","maxFeePerGas":"0x3b9aca00","maxPriorityFeePerGas":"0x0","data":"0x01","blobVersionedHashes":["0x0122000000000000000000000000000000000000000000000000000000000000"]}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xa9ac1233699bdae25abebae4f9fb54dbb1b44700":{"balance":"0x5af3107a4000","nonce":"0x0"},"0xc200000000000000000000000000000000000000":{"code":"0x600060006000aa"}}""");

        string serialized = await test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(),
            Is.EqualTo("failed with 100000 gas: insufficient funds for gas * price + value: address 0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700 have 100000000000000 want 100000000131072"),
            $"the funded run fills the blob fee cap with the next block's blob base fee of 1: {serialized}");
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

    /// <remarks>
    /// A type-6 transaction reaches the frame estimator and the processor on its type alone, and
    /// eth_estimateGas probes the transaction before estimating it, so an absent frame list must be
    /// reported to the caller rather than faulting on the probe.
    /// </remarks>
    [TestCase("""{"from":"0x0001020304050607080910111213141516171819","to":"0x0000000000000000000000000000000000000000","type":"0x6"}""")]
    [TestCase("""{"from":"0x0001020304050607080910111213141516171819","to":"0x0000000000000000000000000000000000000000","type":"0x6","frames":[]}""")]
    [TestCase("""{"from":"0x0001020304050607080910111213141516171819","type":"0x6"}""")]
    [TestCase("""{"from":"0x0001020304050607080910111213141516171819","type":"0x6","frames":[]}""")]
    public async Task Eth_estimateGas_frame_transaction_without_frames_returns_error(string txJson)
    {
        TestSpecProvider specProvider = new(Eip8141Prototype.Instance);
        using Context ctx = await Context.Create(specProvider);

        object transaction = JsonSerializer.Deserialize<object>(txJson)!;

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest");

        JToken parsed = JToken.Parse(serialized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed["error"]!["code"]!.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(parsed["error"]!["message"]!.Value<string>(), Does.Contain(FrameTxValidation.MissingFrames));
        }
    }

    [Test]
    public async Task Eth_estimateGas_self_recursive_call_until_exhaustion_does_not_return_internal_error()
    {
        // 0x5f5f5f5f5f305af1 = PUSH0 x5, ADDRESS, GAS, CALL: the contract CALLs itself with all remaining gas
        // until the 63/64 rule or the depth limit stops the recursion; an unbalanced frame tracer once surfaced
        // this as -32603 "Stack empty." on 2.0.0-rc.
        object? transaction = JsonSerializer.Deserialize<object>("""{"to":"0x00000000000000000000000000000000000000aa"}""");
        object? stateOverride = JsonSerializer.Deserialize<object>("""{"0x00000000000000000000000000000000000000aa":{"code":"0x5f5f5f5f5f305af1"}}""");

        TestSpecProvider specProvider = new(Prague.Instance);
        using Context ctx = await Context.Create(specProvider);

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "latest", stateOverride);

        Assert.That(serialized, Does.Not.Contain("-32603"), serialized);
        Assert.That(serialized, Does.Contain("\"result\":\"0x"), serialized);
    }
}
