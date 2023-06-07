// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMulticallTestsBlocksAndTransactions
{
    private Transaction GetTransferTxData(UInt256 Nonce, IEthereumEcdsa ethereumEcdsa, PrivateKey From, Address To,
        UInt256 ammount)
    {
        Transaction tx = new()
        {
            Value = ammount,
            Nonce = Nonce,
            GasLimit = 3_000_000,
            SenderAddress = From.Address,
            To = To,
            GasPrice = 20.GWei()
        };

        ethereumEcdsa.Sign(TestItem.PrivateKeyB, tx);
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    ///     We test on blocks before current head and after it,
    ///     Note that if we get blocks before head we set simulation start state to one of that first block
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_eth_moved()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTests.CreateChain();

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

        MultiCallBlockStateCallsModel[] requestMultiCall =
        {
            new()
            {
                BlockOverride =
                    new BlockOverride
                    {
                        Number = (UInt256)new decimal(2),
                        GasLimit = 5_000_000,
                        FeeRecipient = TestItem.AddressC,
                        BaseFee = 0
                    },
                Calls = new[]
                {
                    CallTransactionModel.FromTransaction(txAtoB1), CallTransactionModel.FromTransaction(txAtoB2)
                }
            },
            new()
            {
                BlockOverride =
                    new BlockOverride
                    {
                        Number = (UInt256)new decimal(chain.Bridge.HeadBlock.Number + 10000),
                        GasLimit = 5_000_000,
                        FeeRecipient = TestItem.AddressC,
                        BaseFee = 0
                    },
                Calls = new[]
                {
                    CallTransactionModel.FromTransaction(txAtoB3), CallTransactionModel.FromTransaction(txAtoB4)
                }
            }
        };

        //Test that transfer tx works on mainchain
        UInt256 before = chain.State.GetAccount(TestItem.AddressA).Balance;
        await chain.AddBlock(true, txMainnetAtoB);
        UInt256 after = chain.State.GetAccount(TestItem.AddressA).Balance;
        Assert.Less(after, before);

        TxReceipt recept = chain.Bridge.GetReceipt(txMainnetAtoB.Hash);
        LogEntry[]? ls = recept.Logs;

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        EthRpcModule.MultiCallTxExecutor executor = new(chain.DbProvider, chain.SpecProvider, new JsonRpcConfig());
        ResultWrapper<MultiCallBlockResult[]> result =
            executor.Execute(1, requestMultiCall, BlockParameter.Latest, true);
        MultiCallBlockResult[] data = result.Data;

        Assert.AreEqual(data.Length, 2);

        foreach (MultiCallBlockResult blockResult in data)
        {
            Assert.AreEqual(blockResult.Calls.Length, 2);
        }
    }

    /// <summary>
    ///     This test verifies that a temporary forked blockchain can make transactions, blocks and report on them
    /// </summary>
    [Test]
    public async Task Test_eth_multicall_transactions_forced_fail()
    {
        TestRpcBlockchain chain = await EthRpcMulticallTests.CreateChain();

        UInt256 nonceA = chain.State.GetNonce(TestItem.AddressA);

        Transaction txMainnetAtoB =
            GetTransferTxData(nonceA, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, 1);
        //shall be Ok
        Transaction txAtoB1 =
            GetTransferTxData(nonceA + 1, chain.EthereumEcdsa, TestItem.PrivateKeyC, TestItem.AddressB, 1);

        //shall fail
        Transaction txAtoB2 =
            GetTransferTxData(nonceA + 2, chain.EthereumEcdsa, TestItem.PrivateKeyA, TestItem.AddressB, UInt256.MaxValue);

        MultiCallBlockStateCallsModel[] requestMultiCall =
        {
            new()
            {
                BlockOverride =
                    new BlockOverride
                    {
                        Number = (UInt256)new decimal(chain.Bridge.HeadBlock.Number + 10),
                        GasLimit = 5_000_000,
                        FeeRecipient = TestItem.AddressC,
                        BaseFee = 0
                    },
                Calls = new[]
                {
                    CallTransactionModel.FromTransaction(txAtoB1)
                }
            },
            new()
            {
                BlockOverride =
                    new BlockOverride
                    {
                        Number = (UInt256)new decimal(123),
                        GasLimit = 5_000_000,
                        FeeRecipient = TestItem.AddressC,
                        BaseFee = 0
                    },
                Calls = new[]
                {
                    CallTransactionModel.FromTransaction(txAtoB2)
                }
            }
        };

        //Test that transfer tx works on mainchain
        UInt256 before = chain.State.GetAccount(TestItem.AddressA).Balance;
        await chain.AddBlock(true, txMainnetAtoB);
        UInt256 after = chain.State.GetAccount(TestItem.AddressA).Balance;
        Assert.Less(after, before);

        TxReceipt recept = chain.Bridge.GetReceipt(txMainnetAtoB.Hash);
        LogEntry[]? ls = recept.Logs;

        //Force persistancy of head block in main chain
        chain.BlockTree.UpdateMainChain(new[] { chain.BlockFinder.Head }, true, true);
        chain.BlockTree.UpdateHeadBlock(chain.BlockFinder.Head.Hash);

        //will mock our GetCachedCodeInfo function - it shall be called 3 times if redirect is working, 2 times if not
        EthRpcModule.MultiCallTxExecutor executor = new(chain.DbProvider, chain.SpecProvider, new JsonRpcConfig());
        ResultWrapper<MultiCallBlockResult[]> result =
            executor.Execute(1, requestMultiCall, BlockParameter.Latest, true);
        Assert.AreEqual(ErrorCodes.InternalError, result.ErrorCode);
        Assert.IsTrue(result.Result.Error.StartsWith("At block 123 got exception:"));
    }
}
