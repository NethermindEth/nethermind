// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class GetInclusionListTransactionsHandlerTests
{
    private const ulong BogotaTimestamp = 1_000_000;

    private static GetInclusionListTransactionsHandler BuildHandler(bool focilScheduled)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([]);

        IReleaseSpec preBogota = Substitute.For<IReleaseSpec>();
        preBogota.IsEip7805Enabled.Returns(false);
        IReleaseSpec bogota = Substitute.For<IReleaseSpec>();
        bogota.IsEip7805Enabled.Returns(true);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>())
            .Returns(ci => focilScheduled && ci.ArgAt<ForkActivation>(0).Timestamp >= BogotaTimestamp ? bogota : preBogota);

        return new GetInclusionListTransactionsHandler(pool, specProvider);
    }

    // The committee builds a block's list before that block exists, and a missed slot moves its
    // timestamp, so estimating it from the head can refuse the activation slot itself. Only whether
    // the chain schedules FOCIL at all is decidable here.
    [TestCase(true, true)]
    [TestCase(false, false)]
    public void Fork_gate_follows_whether_the_chain_schedules_focil(bool focilScheduled, bool supported)
    {
        ResultWrapper<InclusionListBytes> result = BuildHandler(focilScheduled).Handle();

        Assert.That(result.Result.ResultType, Is.EqualTo(supported ? ResultType.Success : ResultType.Failure));
        if (!supported)
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }
}
