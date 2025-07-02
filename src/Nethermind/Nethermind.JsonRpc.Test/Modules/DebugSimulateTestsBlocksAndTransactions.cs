// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class DebugSimulateTestsBlocksAndTransactions
{

    [Test]
    public async Task Test_debug_simulate_serialisation()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateSerialisationPayload(chain);

        //Force persistence of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<GethLikeTxTrace> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new GethStyleSimulateBlockTracerFactory(GethTraceOptions.Default));
        ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>> data = result.Data;
        Assert.That(data.Count, Is.EqualTo(7));

        SimulateBlockResult<GethLikeTxTrace> blockResult = data.Last();
        blockResult.Traces.Select(static c => c.Failed).Should().BeEquivalentTo([false, false]);
    }


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    ///     We test on blocks before current head and after it,
    ///     Note that if we get blocks before head we set simulation start state to one of that first block
    /// </summary>
    [Test]
    public async Task Test_debug_simulate_eth_moved()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB = EthSimulateTestsBlocksAndTransactions.GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateEthMovedPayload(chain, nonceA);

        //Test that transfer tx works on mainchain
        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<GethLikeTxTrace> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new GethStyleSimulateBlockTracerFactory(GethTraceOptions.Default));
        ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(9));

        SimulateBlockResult<GethLikeTxTrace> blockResult = data[0];
        Assert.That(blockResult.Traces.Count, Is.EqualTo(2));
        blockResult = data.Last();
        Assert.That(blockResult.Traces.Count, Is.EqualTo(2));
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    /// </summary>
    [Test]
    public async Task Test_debug_simulate_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            EthSimulateTestsBlocksAndTransactions.GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransactionsForcedFail(chain, nonceA);

        //Test that transfer tx works on mainchain
        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<GethLikeTxTrace> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new GethStyleSimulateBlockTracerFactory(GethTraceOptions.Default));

        ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.That(result.Result!.Error!.Contains("higher than sender balance"), Is.True);
    }

    [Test]
    public async Task TestTransferLogsAddress()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        Console.WriteLine("current test: simulateTransferOverBlockStateCalls");
        var result = chain.DebugRpcModule.debug_simulateV1(payload!, BlockParameter.Latest);
        Assert.That(result.Data.First().Traces.First().TxHash == new Core.Crypto.Hash256("0x29ef1b983e391fc801a478e57e6f1df1519daeb23c7d7aa9b247a43170d46ccf"));
    }

    [Test]
    public async Task TestSerializationDebugSimulate()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonRpcResponse response = await RpcTest.TestRequest(chain.DebugRpcModule, "debug_simulateV1", payload!, "latest");
        response.Should().BeOfType<JsonRpcSuccessResponse>();
        JsonRpcSuccessResponse successResponse = (JsonRpcSuccessResponse)response;
        IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>> data = (IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>)successResponse.Result!;
        Assert.That(data.First().Traces.First().TxHash == new Core.Crypto.Hash256("0x29ef1b983e391fc801a478e57e6f1df1519daeb23c7d7aa9b247a43170d46ccf"));
    }

    [Test]
    public async Task TestSerializationDebugSimulate_enusre_have_traces_no_Calls()
    {
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        string serialized = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_simulateV1", payload!, "latest");
        serialized.Should().Contain("\"traces\":");
    }
}
