// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    [Test]
    public void NativeHistory_RecordedSelection_RetainsOriginAcrossSync([Values(122UL, 124UL, 126UL, 132UL, 134UL, 138UL)] ulong timestamp)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeL1HeaderChain chain = HistoryChain([108, 120, 121, 132, 144]);
        bool afterFirst = timestamp > 126;
        NativeHistoryState state = new(99, afterFirst ? 100UL : 99UL, chain.At(afterFirst ? 100UL : 99UL).Hash, afterFirst ? 132UL : 120UL);
        ulong? selected = timestamp == 126 ? 100UL : timestamp == 138 ? 102UL : null;
        NativeHistoryExecution execution = schedule.Resolve(timestamp, in state, chain, selected);
        ulong origin = timestamp < 126 ? 99UL : timestamp < 138 ? 100UL : 102UL;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Cutoff, Is.EqualTo(timestamp < 126 ? 108UL : timestamp < 138 ? 120UL : 132UL));
            Assert.That(execution.OriginNumber, Is.EqualTo(origin));
            Assert.That(execution.OriginHash, Is.EqualTo(chain.At(origin).Hash));
            Assert.That(execution.PrevRandao, Is.EqualTo(OfflineHash((byte)(origin - 98))));
            Assert.That(execution.Import.IsEmpty, Is.EqualTo(!selected.HasValue));
        }
        if (selected.HasValue)
        {
            ReadOnlySpan<byte> words = NativeHistoryCall.Decode(execution.Import.Span, out ulong cutoff);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cutoff, Is.EqualTo(execution.Cutoff));
                for (int index = 0; index < words.Length / 32; index++)
                    Assert.That(new ValueHash256(words.Slice(index * 32, 32)), Is.EqualTo(chain.At(state.Endpoint + (ulong)index + 1).Hash));
            }
        }
    }

    [Test]
    public void NativeHistory_EmptyRepeat_IsRecordedAndStillAdvancesCutoff([Values(126UL, 138UL)] ulong timestamp)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeL1HeaderChain chain = HistoryChain([108]);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, timestamp - 6);
        NativeHistoryExecution execution = schedule.Resolve(timestamp, in state, chain, 99);
        ReadOnlySpan<byte> hashes = NativeHistoryCall.Decode(execution.Import.Span, out ulong cutoff);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.OriginNumber, Is.EqualTo(99UL));
            Assert.That(execution.PrevRandao, Is.EqualTo(OfflineHash(1)));
            Assert.That(execution.Import.Length, Is.EqualTo(100));
            Assert.That(cutoff, Is.EqualTo(timestamp - 6));
            Assert.That(hashes.IsEmpty, Is.True);
        }
        NativeHistoryState stale = state with { ScheduleCutoff = state.ScheduleCutoff - 12 };
        Assert.That(() => schedule.Resolve(timestamp, in stale, chain, 99), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeHistory_MaximumImport_IsExact()
    {
        ulong[] times = new ulong[66];
        for (int index = 0; index < times.Length; index++) times[index] = (ulong)index + 1;
        NativeL1HeaderChain chain = HistoryChain(times);
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, 120);
        NativeHistoryExecution execution = schedule.Resolve(126, in state, chain, 163);
        ReadOnlySpan<byte> hashes = NativeHistoryCall.Decode(execution.Import.Span, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hashes.Length, Is.EqualTo(64 * 32));
            Assert.That(new ValueHash256(hashes[..32]), Is.EqualTo(chain.At(100).Hash));
            Assert.That(new ValueHash256(hashes[^32..]), Is.EqualTo(chain.At(163).Hash));
            Assert.That(() => schedule.Resolve(126, in state, chain, 164), Throws.TypeOf<EezSettlementException>());
        }
    }

    private static readonly string[] InvalidHistorySelections =
        ["genesis", "alignment", "first", "next", "endpoint-hash", "missing-selection", "off-schedule", "future", "regression", "missing-endpoint", "missing-source", "overflow", "idle-overflow"];

    [Test]
    public void NativeHistoryCall_ChainRange_MatchesTheSpanEncoder([Values(99UL, 100UL, 103UL)] ulong selected)
    {
        NativeL1HeaderChain chain = HistoryChain([108, 120, 121, 132, 144]);
        ValueHash256[] hashes = new ValueHash256[(int)(selected - 99)];
        for (int index = 0; index < hashes.Length; index++) hashes[index] = chain.At(100UL + (ulong)index).Hash;
        Assert.That(NativeHistoryCall.Encode(132, chain, 99, selected), Is.EqualTo(NativeHistoryCall.Encode(132, hashes)));
    }

    [Test]
    public void NativeHistoryCall_InvalidChainRange_Rejects([Values("regression", "excess", "wrap")] string field)
    {
        NativeL1HeaderChain chain = HistoryChain([108]);
        ulong after = field == "wrap" ? ulong.MaxValue : 99UL;
        ulong selected = field == "regression" ? 98UL : field == "excess" ? 164UL : 0UL;
        Assert.That(() => NativeHistoryCall.Encode(132, chain, after, selected), Throws.TypeOf<EezAbiException>());
    }

    [Test]
    public void NativeHistory_InvalidParentOrSelection_Rejects([ValueSource(nameof(InvalidHistorySelections))] string field)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        NativeL1HeaderChain chain = HistoryChain([108, 120, 121, 132, 144]);
        NativeHistoryState state = new(99, 99, chain.At(99).Hash, 120);
        ulong timestamp = 126;
        ulong? selected = 100;
        switch (field)
        {
            case "genesis": timestamp = 120; selected = null; break;
            case "alignment": timestamp = 125; break;
            case "first": state = state with { FirstNumber = 100 }; break;
            case "next": state = state with { ScheduleCutoff = 132 }; break;
            case "endpoint-hash": state = state with { EndpointHash = OfflineHash(0x77) }; break;
            case "missing-selection": selected = null; break;
            case "off-schedule": timestamp = 124; selected = 99; break;
            case "future": selected = 101; break;
            case "regression": state = state with { Endpoint = 100, EndpointHash = chain.At(100).Hash }; selected = 99; break;
            case "missing-endpoint": state = state with { FirstNumber = 98, Endpoint = 98 }; break;
            case "missing-source": selected = 110; break;
            case "overflow" or "idle-overflow":
                bool idle = field == "idle-overflow";
                schedule = new NativeHistorySchedule(ulong.MaxValue - 6, 2, 6, idle ? 0U : 2U);
                state = state with { ScheduleCutoff = idle ? 5UL : ulong.MaxValue - 6 };
                timestamp = idle ? ulong.MaxValue - 2 : ulong.MaxValue;
                selected = idle ? null : 99UL;
                break;
        }
        Assert.That(() => schedule.Resolve(timestamp, in state, chain, selected), Throws.TypeOf<EezSettlementException>());
    }

    private static NativeL1HeaderChain HistoryChain(ulong[] times)
    {
        NativeL1HeaderChain chain = new();
        chain.Append(L1ChainBytes(times), LegacyHeaderRules);
        return chain;
    }
}
