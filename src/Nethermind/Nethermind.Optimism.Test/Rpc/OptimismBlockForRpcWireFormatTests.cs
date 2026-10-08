// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Test.Data;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

/// <summary>OP Stack clients read deposit transactions out of full blocks, so their wire format must not move.</summary>
public class OptimismBlockForRpcWireFormatTests
{
    [SetUp]
    public void SetUp()
    {
        TransactionForRpc.RegisterTransactionType<DepositTransactionForRpc>();
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder<Transaction>());
    }

    [Test]
    public void Full_block_with_deposits_keeps_its_wire_format()
    {
        Block block = BuildBlock();

        // As OptimismEthRpcModule.eth_getBlockByNumber builds it: hashes skipped, then the full transactions set.
        BlockForRpc full = new(block, includeFullTransactionData: false, MainnetSpecProvider.Instance, skipTxs: true);
        TransactionForRpc[] transactions = new TransactionForRpc[block.Transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            TransactionForRpc rpc = TransactionForRpc.FromTransaction(block.Transactions[i],
                new(BlockchainIds.Mainnet, block.Hash!, block.Number, i, block.Timestamp, block.BaseFeePerGas));
            if (rpc is DepositTransactionForRpc deposit) deposit.DepositReceiptVersion = 1;
            transactions[i] = rpc;
        }

        full.Transactions = transactions;

        using (Assert.EnterMultipleScope())
        {
            JsonFixture.AssertMatches(typeof(OptimismBlockForRpcWireFormatTests).Assembly, "optimism-full", BlockForRpcWireFormatTests.Serialize(full));
            JsonFixture.AssertMatches(typeof(OptimismBlockForRpcWireFormatTests).Assembly, "optimism-hashes",
                BlockForRpcWireFormatTests.Serialize(new BlockForRpc(block, includeFullTransactionData: false, MainnetSpecProvider.Instance)));
        }
    }

    private static Block BuildBlock()
    {
        Transaction deposit = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithGasLimit(0x1234)
            .WithValue(0x1)
            .WithData([0x61, 0x62])
            .WithSourceHash(Hash256.Zero)
            .WithSenderAddress(Address.FromNumber(1))
            .WithHash(new Hash256("0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"))
            .TestObject;
        Transaction regular = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(30).SignedAndResolved().TestObject;
        return Build.A.Block.WithNumber(3).WithBaseFeePerGas(7).WithTransactions(deposit, regular).TestObject;
    }
}
