// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
internal class XdcBlockProcessorTests
{
    private TestableXdcBlockProcessor _processor = null!;

    [SetUp]
    public void SetUp() =>
        _processor = new TestableXdcBlockProcessor();

    [
        TestCase(0L, "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
        TestCase(1L, "0x5fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd2"),
        TestCase(2L, "0xf2ee15ea639b73fa3db9b34a245bdfa015c260c598b211bf05a1ecc4b3e3b4f2"),
        TestCase(100L, "0xf1918e8562236eb17adc8502332f4c9c82bc14e19bfc0aa10ab674ff75b3d2f3"),
        TestCase(10_000_000L, "0xcb2bea23225e166d181633a5500bb13f45947baf4699fe7d7be7709662ed2cd4"),
        TestCase(50_000_000L, "0xcfa4f717b73afc03b1397871f456c85553ea93c536860d57572d2f2b6947e962"),
        TestCase(999_999_999L, "0xecfe0cf238d62038a8de7039c5c1700a26581c9c3c61e6352085728207948d1f"),
    ]
    public void CreateBlockExecutionContext_PrevRandao_MatchesGoXdcRandomValue(long blockNumber, string expectedPrevRandaoHex)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();

        BlockExecutionContext ctx = _processor.CreateBlockExecutionContext(header, spec);

        ctx.PrevRandao.Should().Be(new ValueHash256(expectedPrevRandaoHex));
    }

    private class TestableXdcBlockProcessor : XdcBlockProcessor
    {
        public TestableXdcBlockProcessor() : base(
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockValidator>(),
            Substitute.For<IRewardCalculator>(),
            Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            Substitute.For<IWorldState>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IBeaconBlockRootHandler>(),
            Substitute.For<IBlockhashStore>(),
            NullLogManager.Instance,
            Substitute.For<IWithdrawalProcessor>(),
            Substitute.For<IExecutionRequestsProcessor>())
        { }

        public new BlockExecutionContext CreateBlockExecutionContext(BlockHeader header, IReleaseSpec spec)
            => base.CreateBlockExecutionContext(header, spec);
    }
}
