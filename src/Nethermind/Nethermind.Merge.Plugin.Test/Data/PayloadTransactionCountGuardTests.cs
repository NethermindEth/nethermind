// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Data;

[TestFixture]
public class PayloadTransactionCountGuardTests
{
    private const ulong ParentGasLimit = 30_000_000;

    private const ulong Eip1559TransitionBlock = 12_965_000;

    // (30M + 30M / 1024) / 10,500; doubled by the elasticity multiplier at the EIP-1559 fork block.
    [TestCase(Eip1559TransitionBlock + 1, 2_859, true)]
    [TestCase(Eip1559TransitionBlock + 1, 2_860, false)]
    [TestCase(Eip1559TransitionBlock, 5_719, true)]
    [TestCase(Eip1559TransitionBlock, 5_720, false)]
    public void Bounds_count_by_parent_gas_limit(ulong blockNumber, int count, bool expected)
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(TestItem.KeccakA, BlockTreeLookupOptions.DoNotCreateLevelIfMissing)
            .Returns(Build.A.BlockHeader.WithGasLimit(ParentGasLimit).TestObject);

        bool result = new PayloadTransactionCountGuard(blockFinder).IsWithinParentGasLimit(Payload(count, blockNumber), London.Instance, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(error, expected ? Is.Null : Does.StartWith("Transaction count"));
        }
    }

    [Test]
    public void Allows_any_count_when_disabled_or_parent_unknown([Values] bool disabled)
    {
        PayloadTransactionCountGuard guard = disabled
            ? PayloadTransactionCountGuard.Disabled
            : new PayloadTransactionCountGuard(Substitute.For<IBlockFinder>());

        Assert.That(guard.IsWithinParentGasLimit(Payload(100_000), Osaka.Instance, out _), Is.True);
    }

    private static ExecutionPayload Payload(int count, ulong blockNumber = 0) =>
        new() { ParentHash = TestItem.KeccakA, BlockNumber = blockNumber, Transactions = new byte[count][] };
}
