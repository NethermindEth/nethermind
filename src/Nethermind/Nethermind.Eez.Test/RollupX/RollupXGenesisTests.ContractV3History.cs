// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private static readonly ulong[] ContractV3OmissionTimestamps = [122, 124, 126, 132, 134, 138];

    [Test]
    public void ContractV3History_OmissionInheritsOriginAtAndBetweenEligiblePositions(
        [ValueSource(nameof(ContractV3OmissionTimestamps))] ulong timestamp)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeL1HeaderChain chain = HistoryChain([108, 120, 121, 132, 144]);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, 120);
        NativeHistoryExecution execution = schedule.ResolveContractV3(timestamp, in state, chain, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.OriginNumber, Is.EqualTo(99UL));
            Assert.That(execution.OriginHash, Is.EqualTo(chain.At(99).Hash));
            Assert.That(execution.PrevRandao, Is.EqualTo(chain.At(99).PrevRandao));
            Assert.That(execution.Import.IsEmpty, Is.True);
        }
    }

    [Test]
    public void ContractV3History_FutureOriginCannotBeSelected()
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeL1HeaderChain chain = HistoryChain([108, 120, 121]);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, 120);
        Assert.That(() => schedule.ResolveContractV3(126, in state, chain, 101), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void ContractV3History_Exactly64HeadersAreAllowedBut65AreRejected()
    {
        ulong[] times = new ulong[66];
        for (int index = 0; index < times.Length; index++) times[index] = (ulong)index + 1;
        NativeL1HeaderChain chain = HistoryChain(times);
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, 120);
        Assert.That(schedule.ResolveContractV3(126, in state, chain, 163).Import.Length, Is.EqualTo(100 + 64 * 32));
        Assert.That(() => schedule.ResolveContractV3(126, in state, chain, 164), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void ContractV3History_OnlyNonemptyEligibleImportsAdvance([Values(126UL, 138UL)] ulong timestamp)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeL1HeaderChain chain = HistoryChain([108, 120, 121, 132, 144]);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, 120);
        NativeHistoryExecution execution = schedule.ResolveContractV3(timestamp, in state, chain, 100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Import.Length, Is.EqualTo(132));
            Assert.That(execution.OriginHash, Is.EqualTo(chain.At(100).Hash));
            Assert.That(() => schedule.ResolveContractV3(timestamp, in state, chain, 99), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => schedule.ResolveContractV3(timestamp + 2, in state, chain, 100), Throws.TypeOf<EezSettlementException>());
        }
        NativeHistoryState advanced = state with { Endpoint = 100, EndpointHash = chain.At(100).Hash };
        Assert.That(schedule.ResolveContractV3(timestamp + 2, in advanced, chain, null).OriginNumber, Is.EqualTo(100UL));
        NativeHistoryState wrong = advanced with { ScheduleCutoff = 132 };
        Assert.That(() => schedule.ResolveContractV3(timestamp + 2, in wrong, chain, null), Throws.TypeOf<EezSettlementException>());
    }
}
