// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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

        string serialized = await ctx.Test.TestEthRpc("eth_gasPrice");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected}\",\"id\":67}}"));
    }

    [TestCaseSource(nameof(GetBlobBaseFeeTestCases))]
    public async Task<string> Eth_blobBaseFee_ShouldGiveCorrectResult(ulong? excessBlobGas)
    {
        using Context ctx = await Context.Create();
        Block[] blocks = [
            Build.A.Block.WithNumber(0).WithExcessBlobGas(excessBlobGas).TestObject,
        ];
        BlockTree blockTree = Build.A.BlockTree(blocks[0]).WithBlocks(blocks).TestObject;
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).Build();

        return await ctx.Test.TestEthRpc("eth_blobBaseFee");
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

        string serialized = await ctx.Test.TestEthRpc("eth_gasPrice");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected}\",\"id\":67}}"));
    }

    private static Block[] GetThreeTestBlocks(bool eip1559Enabled = true)
    {
        Block firstBlock = Build.A.Block.WithNumber(0).WithParentHash(Keccak.Zero)
            .WithExcessBlobGas(eip1559Enabled ? Eip4844Constants.MaxBlobGasPerBlock * 4 : null)
            .WithTransactions(
            Build.A.Transaction.WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
            Build.A.Transaction.WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject
        ).TestObject;

        Block secondBlock = Build.A.Block.WithNumber(1).WithParentHash(firstBlock.Hash!)
            .WithExcessBlobGas(eip1559Enabled ? Eip4844Constants.MaxBlobGasPerBlock * 8 : null)
            .WithTransactions(
            Build.A.Transaction.WithGasPrice(3).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
            Build.A.Transaction.WithGasPrice(4).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(0).TestObject
        ).TestObject;

        Block thirdBlock = Build.A.Block.WithNumber(2).WithParentHash(secondBlock.Hash!)
            .WithExcessBlobGas(eip1559Enabled ? Eip4844Constants.MaxBlobGasPerBlock * 12 : null)
            .WithTransactions(
            Build.A.Transaction.WithGasPrice(5).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject,
            Build.A.Transaction.WithGasPrice(6).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(1).TestObject
        ).TestObject;

        return [firstBlock, secondBlock, thirdBlock];
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

        return [firstBlock, secondBlock, thirdBlock];
    }

    public static IEnumerable<TestCaseData> GetBlobBaseFeeTestCases
    {
        get
        {
            static string Success(UInt256 result) => $"{{\"jsonrpc\":\"2.0\",\"result\":\"{result.ToHexString(true)}\",\"id\":67}}";
            static string Fail() => $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32603,\"message\":\"Unable to calculate the current blob base fee\"}},\"id\":67}}";

            yield return new TestCaseData((ulong?)null)
            {
                TestName = "Pre-Cancun block",
                ExpectedResult = Success(0)
            };
            yield return new TestCaseData(0ul)
            {
                TestName = $"Cancun block no {nameof(BlockHeader.ExcessBlobGas)} accumulated",
                ExpectedResult = Success(Eip4844Constants.MinBlobGasPrice)
            };
            yield return new TestCaseData(1ul)
            {
                TestName = $"Low {nameof(BlockHeader.ExcessBlobGas)}",
                ExpectedResult = Success(Eip4844Constants.MinBlobGasPrice)
            };
            yield return new TestCaseData(Eip4844Constants.GasPerBlob)
            {
                TestName = $"Low {nameof(BlockHeader.ExcessBlobGas)}",
                ExpectedResult = Success(Eip4844Constants.MinBlobGasPrice)
            };
            yield return new TestCaseData(Eip4844Constants.MaxBlobGasPerBlock * 4)
            {
                TestName = "Initial price spike",
                ExpectedResult = Success(2)
            };
            yield return new TestCaseData(Eip4844Constants.MaxBlobGasPerBlock * 42)
            {
                TestName = "Price spike",
                ExpectedResult = Success(19806)
            };
            yield return new TestCaseData(Eip4844Constants.MaxBlobGasPerBlock * 419)
            {
                TestName = $"Price spike higher than {nameof(UInt64)} value",
                ExpectedResult = Success(UInt256.Parse("0x54486950184d094e079641e7e0d6dd85a81c"))
            };
            yield return new TestCaseData(Eip4844Constants.MaxBlobGasPerBlock * 3000)
            {
                TestName = $"Overflow for huge {nameof(BlockHeader.ExcessBlobGas)} value",
                ExpectedResult = Fail()
            };
            yield return new TestCaseData(ulong.MaxValue)
            {
                TestName = $"Overflow for max {nameof(BlockHeader.ExcessBlobGas)} value",
                ExpectedResult = Fail()
            };
        }
    }
}
