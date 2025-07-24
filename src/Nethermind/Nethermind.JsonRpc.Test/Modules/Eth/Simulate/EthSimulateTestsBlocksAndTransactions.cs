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
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthSimulateTestsBlocksAndTransactions
{
    public static SimulatePayload<TransactionForRpc> CreateSerialisationPayload(TestRpcBlockchain chain)
    {
        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txToFail = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 10_000_000);
        UInt256 nextNonceA = ++nonceA;
        Transaction tx = GetTransferTxData(nextNonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 4_000_000);

        return new()
        {
            BlockStateCalls =
            [
                new()
                {
                    BlockOverrides = new BlockOverride { Number = 10 },
                    Calls = [TransactionForRpc.FromTransaction(txToFail), TransactionForRpc.FromTransaction(tx)],
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { TestItem.AddressA, new AccountOverride { Balance = Math.Max(420_000_004_000_001UL, 1_000_000_004_000_001UL) } }
                    }
                }
            ],
            TraceTransfers = true,
            Validation = true
        };
    }

    public static SimulatePayload<TransactionForRpc> CreateEthMovedPayload(TestRpcBlockchain chain, UInt256 nonceA)
    {
        Transaction txAtoB1 = GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB2 = GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB3 = GetTransferTxData(nonceA + 3, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB4 = GetTransferTxData(nonceA + 4, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        return new()
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
                    Calls = [TransactionForRpc.FromTransaction(txAtoB1), TransactionForRpc.FromTransaction(txAtoB2)
                    ]
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
                    Calls = [TransactionForRpc.FromTransaction(txAtoB3), TransactionForRpc.FromTransaction(txAtoB4)]
                }
            },
            TraceTransfers = true
        };
    }

    public static SimulatePayload<TransactionForRpc> CreateTransactionsForcedFail(TestRpcBlockchain chain, UInt256 nonceA)
    {
        //shall be Ok
        Transaction txAtoB1 =
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        //shall fail
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, UInt256.MaxValue);

        LegacyTransactionForRpc transactionForRpc = (LegacyTransactionForRpc)TransactionForRpc.FromTransaction(txAtoB2);
        transactionForRpc.Nonce = null;
        LegacyTransactionForRpc transactionForRpc2 = (LegacyTransactionForRpc)TransactionForRpc.FromTransaction(txAtoB1);
        transactionForRpc2.Nonce = null;

        return new()
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
                    Calls = [transactionForRpc2]
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
    }

    public static Transaction GetTransferTxData(UInt256 nonce, IEthereumEcdsa ethereumEcdsa, PrivateKey from, Address to, UInt256 amount)
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

        SimulatePayload<TransactionForRpc> payload = CreateSerialisationPayload(chain);

        //Force persistence of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = result.Data;
        Assert.That(data.Count, Is.EqualTo(7));

        SimulateBlockResult<SimulateCallResult> blockResult = data.Last();
        blockResult.Calls.Select(static c => c.Status).Should().BeEquivalentTo(new[] { (ulong)ResultType.Success, (ulong)ResultType.Success });

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

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB = GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        SimulatePayload<TransactionForRpc> payload = CreateEthMovedPayload(chain, nonceA);

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
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new SimulateBlockMutatorTracerFactory());
        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(9));

        SimulateBlockResult<SimulateCallResult> blockResult = data[0];
        Assert.That(blockResult.Calls.Count, Is.EqualTo(2));
        blockResult = data.Last();
        Assert.That(blockResult.Calls.Count, Is.EqualTo(2));
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    /// </summary>
    [Test]
    public async Task Test_eth_simulate_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();

        UInt256 nonceA = chain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        SimulatePayload<TransactionForRpc> payload = CreateTransactionsForcedFail(chain, nonceA);

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
        SimulateTxExecutor<SimulateCallResult> executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig(), new SimulateBlockMutatorTracerFactory());

        ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.That(result.Result!.Error!.Contains("insufficient sender balance"), Is.True);
    }


    public static SimulatePayload<TransactionForRpc> CreateTransferLogsAddressPayload()
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
        return serializer.Deserialize<SimulatePayload<TransactionForRpc>>(input);
    }


    [Test]
    public async Task TestTransferLogsAddress()
    {
        SimulatePayload<TransactionForRpc> payload = CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        Console.WriteLine("current test: simulateTransferOverBlockStateCalls");
        var result = chain.EthRpcModule.eth_simulateV1(payload!, BlockParameter.Latest);
        var logs = result.Data.First().Calls.First().Logs.ToArray();
        Assert.That(logs.First().Address == new Address("0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE"));
    }

    [Test]
    public async Task TestSerializationEthSimulate()
    {
        SimulatePayload<TransactionForRpc> payload = CreateTransferLogsAddressPayload();
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        JsonRpcResponse response = await RpcTest.TestRequest(chain.EthRpcModule, "eth_simulateV1", payload!, "latest");
        response.Should().BeOfType<JsonRpcSuccessResponse>();
        JsonRpcSuccessResponse successResponse = (JsonRpcSuccessResponse)response;
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> data = (IReadOnlyList<SimulateBlockResult<SimulateCallResult>>)successResponse.Result!;
        var logs = data[0].Calls.First().Logs.ToArray();
        Assert.That(logs.First().Address == new Address("0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE"));
    }
}
