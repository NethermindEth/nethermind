// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthSimulateTestsBlocksAndTransactions
{
    private static Transaction GetTransferTxData(UInt256 nonce, IEthereumEcdsa ethereumEcdsa, PrivateKey from, Address to, UInt256 amount)
    {
        Transaction tx = new()
        {
            Value = amount,
            Nonce = nonce,
            GasLimit = 50_000,
            SenderAddress = from.Address,
            To = to,
            GasPrice = 20.GWei()
        };

        ethereumEcdsa.Sign(from, tx);
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
                    BlockOverrides = new BlockOverride { Number = 10 },
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
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new BlocksConfig().SecondsPerSlot);
        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult> data = result.Data;
        Assert.That(data.Count, Is.EqualTo(7));

        SimulateBlockResult blockResult = data.Last();
        blockResult.Calls.Select(c => c.Status).Should().BeEquivalentTo(new[] { (ulong)ResultType.Success, (ulong)ResultType.Success });

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
        Transaction txAtoB1 = GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB2 = GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB3 = GetTransferTxData(nonceA + 3, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB4 = GetTransferTxData(nonceA + 4, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new List<BlockStateCall<TransactionForRpc>>
            {
                new()
                {
                    BlockOverrides =
                        new BlockOverride
                        {
                            Number = (ulong)chain.BlockFinder.Head!.Number+2,
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
                            Number = (ulong)checked(chain.Bridge.HeadBlock.Number + 10),
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
        UInt256 before = chain.State.GetBalance(TestItem.AddressA);
        await chain.AddBlock(true, txMainnetAtoB);
        UInt256 after = chain.State.GetBalance(TestItem.AddressA);
        Assert.Less(after, before);

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new BlocksConfig().SecondsPerSlot);
        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(9));

        SimulateBlockResult blockResult = data[0];
        Assert.That(blockResult.Calls.Count(), Is.EqualTo(2));
        blockResult = data.Last();
        Assert.That(blockResult.Calls.Count(), Is.EqualTo(2));
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
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        //shall fail
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, UInt256.MaxValue);
        TransactionForRpc transactionForRpc = new(txAtoB2) { Nonce = null };
        TransactionForRpc transactionForRpc2 = new(txAtoB1) { Nonce = null };
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new List<BlockStateCall<TransactionForRpc>>
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
        UInt256 before = chain.State.GetBalance(TestItem.AddressA);
        await chain.AddBlock(true, txMainnetAtoB);
        UInt256 after = chain.State.GetBalance(TestItem.AddressA);
        Assert.Less(after, before);

        chain.Bridge.GetReceipt(txMainnetAtoB.Hash!);

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new BlocksConfig().SecondsPerSlot);

        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.IsTrue(result.Result!.Error!.Contains("higher than sender balance"));
    }


    [Test]
    public async Task TestTransferLogsAddress()
    {
        EthereumJsonSerializer serializer = new();
        string input = """
                       {
                              "traceTransfers": true,
                        "blockStateCalls": [
                          {
                            "blockOverrides": {
                              "baseFeePerGas": "0xa"
                            },
                            "stateOverrides": {
                              "0xc000000000000000000000000000000000000000": {
                                "balance": "0x35a4ece8"
                              }
                            },
                            "calls": [
                              {
                                "from": "0xc000000000000000000000000000000000000000",
                                "to": "0xc100000000000000000000000000000000000000",
                                "gas": "0x5208",
                                "maxFeePerGas": "0x14",
                                "maxPriorityFeePerGas": "0x1",
                                "maxFeePerBlobGas": "0x0",
                                "value": "0x65",
                                "nonce": "0x0",
                                "input": "0x"
                              }
                            ]
                          }
                        ]
                       }
                       """;
        var payload = serializer.Deserialize<SimulatePayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        Console.WriteLine("current test: simulateTransferOverBlockStateCalls");
        var result = chain.EthRpcModule.eth_simulateV1(payload!, BlockParameter.Latest);
        var logs = result.Data.First().Calls.First().Logs.ToArray();
        Assert.That(logs.First().Address == new Address("0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE"));
    }

    [Test]
    public async Task Test_eth_simulate_logs_on_selfdestructive_contract_sends_eth()
    {
        EthereumJsonSerializer serializer = new();
        string input = """
                       {
                         "blockStateCalls": [
                           {
                             "stateOverrides": {
                               "0xc200000000000000000000000000000000000000": {
                                 "code": "0x6080604052348015600e575f80fd5b50600436106026575f3560e01c806383197ef014602a575b5f80fd5b60306032565b005b7f38050d3b233a8bf04054497b223b1c3612ee1bca57db5c6b0f030c25b06159f047604051605f91906096565b60405180910390a13373ffffffffffffffffffffffffffffffffffffffff16ff5b5f819050919050565b6090816080565b82525050565b5f60208201905060a75f8301846089565b9291505056fea2646970667358221220b88011e718061f46682b92207452eed0b566bc847189fabea8c80f5a92c4080064736f6c634300081a0033",
                                 "balance": "0x1e8480"
                               }
                             },
                             "calls": [
                               {
                                 "from": "0xc000000000000000000000000000000000000000",
                                 "to": "0xc200000000000000000000000000000000000000",
                                 "input": "0x83197ef0"
                               }
                             ]
                           }
                         ],
                         "traceTransfers": true
                       }
                       """;
        SimulatePayload<TransactionForRpc> payload = serializer.Deserialize<SimulatePayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain(Cancun.Instance);
        ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = chain.EthRpcModule.eth_simulateV1(payload!, BlockParameter.Latest);
        List<SimulateCallResult> calls = result.Data.First().Calls;
        Log[] logs = calls.First().Logs.ToArray();
        logs.Length.Should().BePositive();
    }



}
