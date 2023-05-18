// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.GasPriceOracleTests;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [TestCase(true, "0x4")] //Gas Prices: 1,2,3,4,5,6 | Max Index: 5 | 60th Percentile: 5 * (3/5) = 3 | Result: 4 (0x4)
    [TestCase(false, "0x4")] //Gas Prices: 1,2,3,4,5,6 | Max Index: 5 | 60th Percentile: 5 * (3/5) = 3 | Result: 4 (0x4)
    public async Task Eth_gasPrice_BlocksAvailableLessThanBlocksToCheck_ShouldGiveCorrectResult(bool eip1559Enabled, string expected)
    {
        using Context ctx = await Context.Create();
        Block[] blocks = GetThreeTestBlocks();
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        GasPriceOracle gasPriceOracle = new(blockTree, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).WithGasPriceOracle(gasPriceOracle)
            .Build();

        string serialized = ctx.Test.TestEthRpc("eth_gasPrice");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected}\",\"id\":67}}"));
    }

    [TestCase(true, "0x3")] //Gas Prices: 1,2,3,3,4,5 | Max Index: 5 | 60th Percentile: 5 * (3/5) = 3 | Result: 3 (0x3)
    [TestCase(false, "0x2")] //Gas Prices: 0,1,1,2,2,3 | Max Index: 5 | 60th Percentile: 5 * (3/5) = 3 | Result: 2 (0x2)
    public async Task Eth_gasPrice_BlocksAvailableLessThanBlocksToCheckWith1559Tx_ShouldGiveCorrectResult(bool eip1559Enabled, string expected)
    {
        using Context ctx = await Context.Create();
        Block[] blocks = GetThreeTestBlocksWith1559Tx();
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        GasPriceOracle gasPriceOracle = new(blockTree, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).WithGasPriceOracle(gasPriceOracle)
            .Build();

        string serialized = ctx.Test.TestEthRpc("eth_gasPrice");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected}\",\"id\":67}}"));
    }


    private static Block[] GetThreeTestBlocks()
    {
        Block firstBlock = Build.A.Block.WithNumber(0).WithParentHash(Keccak.Zero).WithTransactions(
            Build.A.Transaction.WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
            Build.A.Transaction.WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject
        ).TestObject;

        Block secondBlock = Build.A.Block.WithNumber(1).WithParentHash(firstBlock.Hash!).WithTransactions(
            Build.A.Transaction.WithGasPrice(3).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
            Build.A.Transaction.WithGasPrice(4).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(0).TestObject
        ).TestObject;

        Block thirdBlock = Build.A.Block.WithNumber(2).WithParentHash(secondBlock.Hash!).WithTransactions(
            Build.A.Transaction.WithGasPrice(5).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject,
            Build.A.Transaction.WithGasPrice(6).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(1).TestObject
        ).TestObject;

        return new[] { firstBlock, secondBlock, thirdBlock };
    }

    private static Block[] GetThreeTestBlocksWith1559Tx()
    {
        Block firstBlock = Build.A.Block.WithNumber(0).WithParentHash(Keccak.Zero).WithBaseFeePerGas(3).WithTransactions(
            Build.A.Transaction.WithMaxFeePerGas(1).WithMaxPriorityFeePerGas(1).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).WithType(TxType.EIP1559).TestObject, //Min(1, 1 + 3) = 1
            Build.A.Transaction.WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).WithType(TxType.EIP1559).TestObject  //Min(2, 2 + 3) = 2
        ).TestObject;

        Block secondBlock = Build.A.Block.WithNumber(1).WithParentHash(firstBlock.Hash!).WithBaseFeePerGas(3).WithTransactions(
            Build.A.Transaction.WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(3).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).WithType(TxType.EIP1559).TestObject, //Min(3, 2 + 3) = 3
            Build.A.Transaction.WithMaxFeePerGas(4).WithMaxPriorityFeePerGas(0).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(0).WithType(TxType.EIP1559).TestObject  //Min(4, 0 + 3) = 3
        ).TestObject;

        Block thirdBlock = Build.A.Block.WithNumber(2).WithParentHash(secondBlock.Hash!).WithBaseFeePerGas(3).WithTransactions(
            Build.A.Transaction.WithMaxFeePerGas(5).WithMaxPriorityFeePerGas(1).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).WithType(TxType.EIP1559).TestObject, //Min(5, 1 + 3) = 4
            Build.A.Transaction.WithMaxFeePerGas(6).WithMaxPriorityFeePerGas(2).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(1).WithType(TxType.EIP1559).TestObject  //Min(6, 2 + 3) = 5
        ).TestObject;

        return new[] { firstBlock, secondBlock, thirdBlock };
    }
}
