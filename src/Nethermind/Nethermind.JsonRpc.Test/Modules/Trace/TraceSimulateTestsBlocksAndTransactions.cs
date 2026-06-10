// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules.Simulate;
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
    public async Task Test_trace_simulate_respects_gas_cap(long gasCap, long? requestGas, bool expectCapped)
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
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
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        Console.WriteLine("current test: simulateTransferOverBlockStateCalls");
        ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> result = chain.TraceRpcModule.trace_simulateV1(payload!, BlockParameter.Latest);
        Assert.That(result.Data.First().Traces.First().BlockHash, Is.EqualTo(new Core.Crypto.Hash256("0x45635998c509d5571fcc391772c5af77f3f202b70ea9fafb48ea8eb475288b59")));
    }

    [Test]
    public async Task TestSerializationEthSimulate()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonRpcResponse response = await RpcTest.TestRequest(chain.TraceRpcModule, "trace_simulateV1", payload!, "latest");
        IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>> data = RpcTest.AssertSuccess<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>>(response);
        Assert.That(data.First().Traces.First().BlockHash, Is.EqualTo(new Core.Crypto.Hash256("0x45635998c509d5571fcc391772c5af77f3f202b70ea9fafb48ea8eb475288b59")));
    }

    [Test]
    public async Task TestSerializationTraceSimulate_ensure_have_traces_no_Calls()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        string serialized = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_simulateV1", payload!, "latest");
        Assert.That(serialized, Does.Contain("\"traces\":"));
    }
}
