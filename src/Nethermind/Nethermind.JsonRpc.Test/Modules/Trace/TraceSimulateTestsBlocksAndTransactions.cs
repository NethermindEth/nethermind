// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Test.Modules.Simulate;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Nethermind.JsonRpc.Test.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules.Eth.Simulate;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

public class TraceSimulateTestsBlocksAndTransactions : TracedSimulateTestsBase<ParityLikeTxTrace>
{
    protected override ISimulateBlockTracerFactory<ParityLikeTxTrace> CreateTracerFactory() =>
        new ParityStyleSimulateBlockTracerFactory(ParityTraceTypes.Trace);

    protected override void AssertSerializationBlockResult(SimulateBlockResult<ParityLikeTxTrace> blockResult) =>
        Assert.That(blockResult.Traces.Select(static c => c.BlockNumber), Is.Not.Null.And.Not.Empty);

    [TestCaseSource(typeof(EthRpcSimulateTestsBase), nameof(EthRpcSimulateTestsBase.GasCapSimulateCases))]
    public async Task Test_trace_simulate_respects_gas_cap(ulong gasCap, ulong? requestGas, bool expectCapped)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        chain.Container.Resolve<IJsonRpcConfig>().GasCap = gasCap;

        ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> result = chain.TraceRpcModule.trace_simulateV1(
            EthRpcSimulateTestsBase.CreateGasProbePayload(requestGas),
            BlockParameter.Latest);
        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        ParityLikeTxTrace trace = result.Data.First().Traces.First();
        Assert.That(trace.Output, Is.Not.Null, "gas probe call should return the remaining-gas result");
        UInt256 gasAvailable = new(trace.Output!, isBigEndian: true);
        if (expectCapped)
        {
            Assert.That(gasAvailable, Is.LessThan((UInt256)gasCap));
        }

        Assert.That(gasAvailable, Is.GreaterThan(UInt256.Zero));
    }

    [Test]
    public async Task TestTransferLogsAddress()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        Console.WriteLine("current test: simulateTransferOverBlockStateCalls");
        ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> result = chain.TraceRpcModule.trace_simulateV1(payload!, BlockParameter.Latest);
        Assert.That(result.Data.First().Traces.First().BlockHash, Is.EqualTo(new Core.Crypto.Hash256("0x899d78f0d7cbc0a6e384edf93ceea71ee9ae96612b3ddb1d99e2a1c1d2ef129f")));
    }

    [Test]
    public async Task TestSerializationEthSimulate()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonRpcResponse response = await RpcTest.TestRequest(chain.TraceRpcModule, "trace_simulateV1", payload!, "latest");
        IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>> data = RpcTest.AssertSuccess<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>>(response);
        Assert.That(data.First().Traces.First().BlockHash, Is.EqualTo(new Core.Crypto.Hash256("0x899d78f0d7cbc0a6e384edf93ceea71ee9ae96612b3ddb1d99e2a1c1d2ef129f")));
    }

    [Test]
    public async Task TestSerializationTraceSimulate_ensure_have_traces_no_Calls()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        string serialized = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_simulateV1", payload!, "latest");
        Assert.That(serialized, Does.Contain("\"traces\":"));
    }

    [Test]
    public async Task State_diff_is_serialized_as_stateDiff()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        string serialized = await RpcTest.TestSerializedRequest(
            chain.TraceRpcModule, "trace_simulateV1", payload!, "latest", new[] { nameof(ParityTraceTypes.StateDiff) });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized, Does.Contain("\"stateDiff\":"));
            Assert.That(serialized, Does.Not.Contain("stateChanges"));
        }
    }

    [TestCase(1, false, TestName = "Trace_simulate_of_one_block_stays_within_the_data_limit")]
    [TestCase(2, true, TestName = "Trace_simulate_keeps_one_data_budget_across_blocks")]
    public async Task Trace_simulate_counts_kept_data_over_all_blocks(int blocks, bool exceeds)
    {
        using TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        chain.Container.Resolve<IJsonRpcConfig>().MaxBufferedTraceLogSize = 3 * (ParityLikeTxTracer.RetainedActionBytes + 100);
        string input = "0x" + new string('a', 120);
        string call = $$"""{ "from": "0xc000000000000000000000000000000000000000", "to": "0xc100000000000000000000000000000000000000", "gas": "0x10000", "input": "{{input}}" }""";
        string blockStateCalls = string.Join(",", Enumerable.Repeat($$"""{ "calls": [ {{call}} ] }""", blocks));
        SimulatePayload<TransactionForRpc> payload = new EthereumJsonSerializer().Deserialize<SimulatePayload<TransactionForRpc>>(
            $$"""{ "blockStateCalls": [ {{blockStateCalls}} ] }""")!;

        string response = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_simulateV1", payload, "latest");

        using JsonDocument document = JsonDocument.Parse(response);
        if (exceeds)
        {
            Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ClientLimitExceededError), response);
        }
        else
        {
            Assert.That(document.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(blocks), response);
        }
    }

    [Test]
    public void Trace_simulate_is_not_sharable()
    {
        JsonRpcMethodAttribute attribute = typeof(ITraceRpcModule).GetMethod(nameof(ITraceRpcModule.trace_simulateV1))!
            .GetCustomAttribute<JsonRpcMethodAttribute>()!;

        Assert.That(attribute.IsSharable, Is.False);
    }
}
