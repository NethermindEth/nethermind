// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
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
        pool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>()).Returns(new Dictionary<AddressAsKey, Transaction[]>());

        IReleaseSpec preBogota = Substitute.For<IReleaseSpec>();
        preBogota.IsEip7805Enabled.Returns(false);
        IReleaseSpec bogota = Substitute.For<IReleaseSpec>();
        bogota.IsEip7805Enabled.Returns(true);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>())
            .Returns(ci => focilScheduled && ci.ArgAt<ForkActivation>(0).Timestamp >= BogotaTimestamp ? bogota : preBogota);

        IChainHeadInfoProvider chainHeadInfo = Substitute.For<IChainHeadInfoProvider>();
        chainHeadInfo.ReadOnlyStateProvider.Returns(Substitute.For<IReadOnlyStateProvider>());

        return new GetInclusionListTransactionsHandler(pool, Substitute.For<IBlockTree>(), specProvider, chainHeadInfo);
    }

    // The list is built before its block exists and a missed slot moves the timestamp, so only whether
    // the chain schedules inclusion lists at all is decidable here.
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
