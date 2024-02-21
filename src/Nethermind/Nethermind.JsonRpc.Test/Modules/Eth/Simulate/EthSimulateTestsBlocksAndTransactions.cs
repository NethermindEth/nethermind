// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthSimulateTestsBlocksAndTransactions
{
    private static Transaction GetTransferTxData(UInt256 nonce, IEthereumEcdsa ethereumEcdsa, PrivateKey from, Address to, UInt256 ammount)
    {
        Transaction tx = new()
        {
            Value = ammount,
            Nonce = nonce,
            GasLimit = 50_000,
            SenderAddress = from.Address,
            To = to,
            GasPrice = 20.GWei()
        };

        ethereumEcdsa.Sign(TestItem.PrivateKeyB, tx);
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    [Test]
    public async Task Test_eth_simulate_serialisation()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.State.GetNonce(TestItem.AddressA);
        Transaction txToFail = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 10_000_000);
        UInt256 nextNonceA = ++nonceA;
        Transaction tx = GetTransferTxData(nextNonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 4_000_000);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { Number = 18000000 },
                    Calls = [new TransactionForRpc(txToFail), new TransactionForRpc(tx)],
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = Math.Max(420_000_004_000_001UL, 1_000_000_004_000_001UL) } }
                    }
                }
            ],
            TraceTransfers = true,
            Validation = true
        };

        //Force persistence of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());
        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult> data = result.Data;
        Assert.That(data.Count, Is.EqualTo(1));

        foreach (SimulateBlockResult blockResult in data)
        {
            blockResult.Calls.Select(c => c.Type).Should().BeEquivalentTo(new[] { (ulong)ResultType.Success, (ulong)ResultType.Success });
        }
    }


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    ///     We test on blocks before current head and after it,
    ///     Note that if we get blocks before head we set simulation start state to one of that first block
    /// </summary>
    [Test]
    public async Task Test_eth_simulate_eth_moved()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.State.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB1 = GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);
        Transaction txAtoB2 = GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB3 = GetTransferTxData(nonceA + 3, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);
        Transaction txAtoB4 = GetTransferTxData(nonceA + 4, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new BlockStateCall<TransactionForRpc>[]
            {
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = 2,
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = new[] { new TransactionForRpc(txAtoB1), new TransactionForRpc(txAtoB2) }
                },
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = (ulong)checked(chain.Bridge.HeadBlock.Number + 10000),
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = new[] { new TransactionForRpc(txAtoB3), new TransactionForRpc(txAtoB4) }
                }
            },
            TraceTransfers = true
        };

        //Test that transfer tx works on mainchain
        UInt256 before = chain.State.GetAccount(TestItem.AddressA).Balance;
        await chain.AddBlock(true, txMainnetAtoB);
        UInt256 after = chain.State.GetAccount(TestItem.AddressA).Balance;
        Assert.Less(after, before);

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());
        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(2));

        foreach (SimulateBlockResult blockResult in data)
        {
            Assert.That(blockResult.Calls.Count(), Is.EqualTo(2));
        }
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    /// </summary>
    [Test]
    public async Task Test_eth_simulate_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.State.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        //shall be Ok
        Transaction txAtoB1 =
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);

        //shall fail
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, UInt256.MaxValue);
        TransactionForRpc transactionForRpc = new(txAtoB2) { Nonce = null };
        TransactionForRpc transactionForRpc2 = new(txAtoB1) { Nonce = null };
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new BlockStateCall<TransactionForRpc>[]
            {
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = (ulong)checked(chain.Bridge.HeadBlock.Number + 10),
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = new[] { transactionForRpc2 }
                },
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = 123,
                            GasLimit = 5_000_000,
                            FeeRecipient = TestItem.AddressC,
                            BaseFeePerGas = 0
                        },
                    Calls = new[] { transactionForRpc }
                }
            },
            TraceTransfers = true,
            Validation = true
        };

        //Test that transfer tx works on mainchain
        UInt256 before = chain.State.GetAccount(TestItem.AddressA).Balance;
        await chain.AddBlock(true, txMainnetAtoB);
        UInt256 after = chain.State.GetAccount(TestItem.AddressA).Balance;
        Assert.Less(after, before);

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());

        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.IsTrue(result.Data[1].Calls.First().Error?.Message.StartsWith("insufficient"));
    }
}
