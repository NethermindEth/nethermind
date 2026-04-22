// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules.Simulate;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class TraceSimulateTestsBlocksAndTransactions : TracedSimulateTestsBase<ParityLikeTxTrace>
{
    protected override ISimulateBlockTracerFactory<ParityLikeTxTrace> CreateTracerFactory() =>
        new ParityStyleSimulateBlockTracerFactory(ParityTraceTypes.Trace);

    protected override void AssertSerializationBlockResult(SimulateBlockResult<ParityLikeTxTrace> blockResult) =>
        Assert.That(blockResult.Traces.Select(static c => c.BlockNumber), Is.Not.Null.And.Not.Empty);

    [Test]
    public async Task Test_trace_simulate_caps_gas_to_gas_cap()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        long gasCap = 50_000;
        chain.Container.Resolve<IJsonRpcConfig>().GasCap = gasCap;

        // Contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN — returns remaining gas
        Address contractAddress = new("0xc200000000000000000000000000000000000000");
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { contractAddress, new AccountOverride { Code = Bytes.FromHexString("0x5a60005260206000f3") } }
                    },
                    Calls =
                    [
                        new LegacyTransactionForRpc
                        {
                            From = TestItem.AddressA,
                            To = contractAddress,
                            Gas = 100_000,
                            GasPrice = 0
                        }
                    ]
                }
            ]
        };

        ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> result = chain.TraceRpcModule.trace_simulateV1(payload, BlockParameter.Latest);
        Assert.That((bool)result.Result, Is.True, result.Result.ToString());

        ParityLikeTxTrace trace = result.Data.First().Traces.First();
        UInt256 gasAvailable = new(trace.Output!, isBigEndian: true);
        Assert.That(gasAvailable, Is.LessThan((UInt256)gasCap));
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
        Assert.That(response, Is.TypeOf<JsonRpcSuccessResponse>());
        JsonRpcSuccessResponse successResponse = (JsonRpcSuccessResponse)response;
        IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>> data = (IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>)successResponse.Result!;
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
