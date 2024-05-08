// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [TestCase(1, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x2da282a8\",\"0x27ee3253\"],\"baseFeePerBlobGas\":[\"0x0\",\"0x0\"],\"gasUsedRatio\":[0.0],\"blobGasUsedRatio\":[0.0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    [TestCase(1, "pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x2da282a8\",\"0x27ee3253\"],\"baseFeePerBlobGas\":[\"0x0\",\"0x0\"],\"gasUsedRatio\":[0.0],\"blobGasUsedRatio\":[0.0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    [TestCase(2, "0x01", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x3b9aca00\",\"0x342770c0\"],\"baseFeePerBlobGas\":[\"0x0\",\"0x0\",\"0x0\"],\"gasUsedRatio\":[0.0,0.0],\"blobGasUsedRatio\":[0.0,0.0],\"oldestBlock\":\"0x0\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"],[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    [TestCase(2, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x3b9aca00\"],\"baseFeePerBlobGas\":[\"0x0\",\"0x0\"],\"gasUsedRatio\":[0.0],\"blobGasUsedRatio\":[0.0],\"oldestBlock\":\"0x0\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    public async Task Eth_feeHistory(long blockCount, string blockParameter, string expected)
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        string serialized = await ctx.Test.TestEthRpc("eth_feeHistory", blockCount.ToString(), blockParameter, "[0,10.5,20,60,90]");
        serialized.Should().Be(expected);
    }

    [TestCaseSource(nameof(FeeHistoryBlobTestCases))]
    public (UInt256[], double[]) Eth_feeHistory_ShouldReturnCorrectBlobValues(ulong?[] excessBlobGas, ulong?[] blobGasUsed)
    {
        Block[] blocks = Enumerable.Range(0, excessBlobGas.Length)
         .Select((i) => Build.A.Block.WithHeader(
             Build.A.BlockHeader
                 .WithNumber(i)
                 .WithParentHash(new Hash256(Math.Max(0, i - 1).ToString("X").PadLeft(64, '0')))
                 .WithExcessBlobGas(excessBlobGas[i])
                 .WithBlobGasUsed(blobGasUsed[i])
                 .TestObject).TestObject
             ).ToArray();


        IBlockTree blockFinder = Substitute.For<IBlockTree>();
        blockFinder.Head.Returns(Build.A.Block.WithNumber(excessBlobGas.Length - 1).TestObject);
        blockFinder.FindBlock(Arg.Any<BlockParameter>(), Arg.Any<bool>())
            .Returns(ci => blocks[(int)(((BlockParameter)ci[0]).BlockNumber ?? 0)]);
        blockFinder.FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>())
                  .Returns(ci => blocks[((Hash256)ci[0]).Bytes[^1]]);

        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Cancun.Instance);
        FeeHistoryOracle oracle = new(blockFinder, receiptStorage, specProvider);

        using ResultWrapper<FeeHistoryResults> result = oracle
            .GetFeeHistory(excessBlobGas.Length, new BlockParameter(blocks.Length - 1), [0.0, 1.0]);

        Assert.That(result.ErrorCode, Is.Zero);
        return (result.Data.BaseFeePerBlobGas.ToArray(), result.Data.BlobGasUsedRatio.ToArray());
    }

    public static IEnumerable<TestCaseData> FeeHistoryBlobTestCases
    {
        get
        {
            yield return new TestCaseData(new ulong?[] { null, null }, new ulong?[] { null, null })
            {
                TestName = "Pre-cancun blocks",
                ExpectedResult = (new UInt256?[] { 0, 0, 0 }, new double?[] { 0.0, 0.0 })
            };

            yield return new TestCaseData(new ulong?[] { null, null, 0 }, new ulong?[] { null, null, 0 })
            {
                TestName = "Empty blocks",
                ExpectedResult = (new UInt256?[] { 0, 0, 1, 1 }, new double?[] { 0.0, 0.0, 0.0 })
            };

            yield return new TestCaseData(
                new ulong?[] { 1,
                    2,
                    0,
                    Eip4844Constants.TargetBlobGasPerBlock,
                    Eip4844Constants.MaxBlobGasPerBlock,
                    Eip4844Constants.MaxBlobGasPerBlock * 4 },
                new ulong?[] { 0,
                    Eip4844Constants.GasPerBlob * 2,
                    Eip4844Constants.MaxBlobGasPerBlock,
                    Eip4844Constants.MaxBlobGasPerBlock,
                    Eip4844Constants.MaxBlobGasPerBlock,
                    Eip4844Constants.MaxBlobGasPerBlock })
            {
                TestName = "Different values",
                ExpectedResult = (
                    new UInt256?[] { 1, 1, 1, 1, 1, 2, 2 },
                    new double?[] { 0.0, 0.33333333333333331, 1.0, 1.0, 1.0, 1.0 }
                    )
            };

            yield return new TestCaseData(new ulong?[] { 49152, 1 }, new ulong?[] { 0, 49152 })
            {
                TestName = "Blocks with arbitary values",
                ExpectedResult = (new UInt256?[] { 1, 1, 1 }, new double?[] { 0.0, 0.0625 })
            };
        }
    }
}
