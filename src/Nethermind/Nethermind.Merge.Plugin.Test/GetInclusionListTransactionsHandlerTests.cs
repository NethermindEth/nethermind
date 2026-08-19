// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class GetInclusionListTransactionsHandlerTests
{
    private const ulong SecondsPerSlot = 12;
    private const ulong BogotaTimestamp = 1_000_000;

    private static GetInclusionListTransactionsHandler BuildHandler(ulong headTimestamp)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([]);

        IReleaseSpec preBogota = Substitute.For<IReleaseSpec>();
        preBogota.IsEip7805Enabled.Returns(false);
        IReleaseSpec bogota = Substitute.For<IReleaseSpec>();
        bogota.IsEip7805Enabled.Returns(true);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>())
            .Returns(ci => ci.ArgAt<ForkActivation>(0).Timestamp >= BogotaTimestamp ? bogota : preBogota);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader()
            .Returns(Build.A.BlockHeader.WithNumber(1).WithTimestamp(headTimestamp).TestObject);

        return new GetInclusionListTransactionsHandler(
            pool, specProvider, blockFinder, new BlocksConfig { SecondsPerSlot = SecondsPerSlot });
    }

    // The committee builds the first Bogota block's list while the head is still the last pre-Bogota
    // block, so gating on the head spec would black out exactly the slot the IL machinery comes online.
    [TestCase(BogotaTimestamp - SecondsPerSlot, true)]
    [TestCase(BogotaTimestamp, true)]
    [TestCase(BogotaTimestamp - (SecondsPerSlot * 8), false)]
    public void Fork_gate_follows_the_block_the_list_is_for(ulong headTimestamp, bool supported)
    {
        ResultWrapper<InclusionListBytes> result = BuildHandler(headTimestamp).Handle();

        Assert.That(result.Result.ResultType, Is.EqualTo(supported ? ResultType.Success : ResultType.Failure));
        if (!supported)
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }
}
