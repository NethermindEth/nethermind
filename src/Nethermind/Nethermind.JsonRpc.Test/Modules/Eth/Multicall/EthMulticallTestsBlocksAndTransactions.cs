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
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.MultiCall.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMulticallTestsBlocksAndTransactions
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
    public async Task Test_eth_multicall_serialisation()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();


        PrivateKey pk = new("0xc7ba1a2892ec0ea1940eebeae739b1effe0543b3104469d5b66625f49ca86e94");

        UInt256 nonceA = chain.State.GetNonce(pk.Address);
        Transaction txMainnetAtoBtoFail =
            GetTransferTxData(nonceA,
                chain.EthereumEcdsa, pk, new Address("0xA143c0eA6f8059f7B3651417ccD2bAA80FC2d4Ab"), 10_000_000);

        UInt256 nextNonceA = nonceA++;
        Transaction txMainnetAtoBToComplete =
            GetTransferTxData(nextNonceA,
                chain.EthereumEcdsa, pk, new Address("0xA143c0eA6f8059f7B3651417ccD2bAA80FC2d4Ab"), 4_000_000);

        MultiCallPayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = new BlockStateCall<TransactionForRpc>[]
            {
                new()
                {
                    BlockOverrides = new BlockOverride() { Number = 18000000 },
                    Calls = new[]
                    {
                        new TransactionForRpc(txMainnetAtoBtoFail),
                        new TransactionForRpc(txMainnetAtoBToComplete),
                    },
                    StateOverrides = new Dictionary<Address, AccountOverride>()
                    {
                        {
                            pk.Address,
                            new AccountOverride()
                            {
                                Balance = Math.Max(420_000_004_000_001UL, 1_000_000_004_000_001UL)
                            }
                        }
                    }
                }
            },
            TraceTransfers = true,
            Validation = true
        };
        EthereumJsonSerializer serializer = new();

        string serializedCall = serializer.Serialize(payload);
        Console.WriteLine(serializedCall);


        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new List<Block> { chain.BlockFinder.Head! }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head!.Hash!);
        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());
        ResultWrapper<IReadOnlyList<MultiCallBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<MultiCallBlockResult> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(1));

        foreach (MultiCallBlockResult blockResult in data)
        {
            blockResult.Calls.Select(c => c.Type).Should().BeEquivalentTo(new[] { ResultType.Failure, ResultType.Success });
        }
    }


    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    ///     We test on blocks before current head and after it,
    ///     Note that if we get blocks before head we set simulation start state to one of that first block
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_eth_moved()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();

        UInt256 nonceA = chain.State.GetNonce(TestItem.AddressA);
        Transaction txMainnetAtoB =
            GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB1 =
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        Transaction txAtoB3 =
            GetTransferTxData(nonceA + 3, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);
        Transaction txAtoB4 =
            GetTransferTxData(nonceA + 4, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);

        MultiCallPayload<TransactionForRpc> payload = new()
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
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());
        ResultWrapper<IReadOnlyList<MultiCallBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        IReadOnlyList<MultiCallBlockResult> data = result.Data;

        Assert.That(data.Count, Is.EqualTo(2));

        foreach (MultiCallBlockResult blockResult in data)
        {
            Assert.That(blockResult.Calls.Count(), Is.EqualTo(2));
        }
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();

        UInt256 nonceA = chain.State.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        //shall be Ok
        Transaction txAtoB1 =
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);

        //shall fail
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, UInt256.MaxValue);
        MultiCallPayload<TransactionForRpc> payload = new()
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
                    Calls = new[] { new TransactionForRpc(txAtoB1) }
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
                    Calls = new[] { new TransactionForRpc(txAtoB2) }
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
        MultiCallTxExecutor executor = new(chain.Bridge, chain.BlockFinder, new JsonRpcConfig());

        ResultWrapper<IReadOnlyList<MultiCallBlockResult>> result =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.IsTrue(result.Data[1].Calls.First().Error?.Message.StartsWith("insufficient"));
    }
}
