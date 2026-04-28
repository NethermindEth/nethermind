// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Simulate;

/// <summary>
/// Base class for debug_simulateV1 and trace_simulateV1 tests that share the same
/// executor-level logic but differ only in the tracer factory and result assertions.
/// </summary>
public abstract class TracedSimulateTestsBase<TTrace>
{
    protected abstract ISimulateBlockTracerFactory<TTrace> CreateTracerFactory();

    /// <summary>
    /// Asserts tracer-specific properties on the last block result of the serialization test.
    /// </summary>
    protected abstract void AssertSerializationBlockResult(SimulateBlockResult<TTrace> blockResult);

    [Test]
    public async Task Test_simulate_serialization()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateSerializationPayload(chain);

        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        SimulateTxExecutor<TTrace> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, CreateTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<TTrace>> data = result.Data;
        Assert.That(data, Has.Count.EqualTo(7));

        SimulateBlockResult<TTrace> blockResult = data.Last();
        AssertSerializationBlockResult(blockResult);
    }

    [Test(Description = """
        Verifies that a temporary forked blockchain can make transactions, blocks and report on them.
        We test on blocks before current head and after it.
        Note that if we get blocks before head, we set simulation start state to one of that first block.
        """)]
    public async Task Test_simulate_eth_moved()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB = EthSimulateTestsBlocksAndTransactions.GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1, type: TxType.Legacy);

        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateEthMovedPayload(chain, nonceA);

        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        SimulateTxExecutor<TTrace> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, CreateTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<TTrace>> data = result.Data;

        Assert.That(data, Has.Count.EqualTo(9));

        SimulateBlockResult<TTrace> blockResult = data[0];
        Assert.That(blockResult.Traces, Has.Count.EqualTo(2));
        blockResult = data.Last();
        Assert.That(blockResult.Traces, Has.Count.EqualTo(2));
    }

    [Test(Description = "Verifies that a temporary forked blockchain can make transactions, blocks and report on them")]
    public async Task Test_simulate_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            EthSimulateTestsBlocksAndTransactions.GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1, type: TxType.Legacy);

        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransactionsForcedFail(chain, nonceA);

        UInt256 before = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        await chain.AddBlock(txMainnetAtoB);
        UInt256 after = chain.ReadOnlyState.GetBalance(TestItem.AddressA);
        Assert.That(after, Is.LessThan(before));

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        SimulateTxExecutor<TTrace> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), chain.SpecProvider, CreateTracerFactory());

        ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.That(result.Result!.Error!, Does.Contain("insufficient funds"));
    }
}
