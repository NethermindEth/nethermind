// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.History.Prestate;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class PoisonedClearMemoTests
{
    /// <summary>
    /// Two threads share one reader:
    /// 1. A probes the account at generation 5 and finds no poisoned clear.
    /// 2. Before A stores that answer, a capture publishes generation 6 with a poisoned clear for the account, and B
    ///    probes and stores "poisoned".
    /// 3. A stores its stale "not poisoned".
    /// 4. Any later read must still go to history, as the history reader itself would, because the stale answer was
    ///    sampled before the publish.
    /// </summary>
    [Test]
    public void RequiresHistory_WhenAProbeRacesAPublish_NeverTrustsTheStaleAnswer()
    {
        long[] memo = new long[1];
        RacingSource source = new() { Generation = 5 };
        bool threadB = false;
        source.DuringProbe = () =>
        {
            source.Generation = 6;
            source.Poisoned = true;
            threadB = PoisonedClearMemo.RequiresHistory(memo, 0, TestItem.AddressA, source);
        };

        bool threadA = PoisonedClearMemo.RequiresHistory(memo, 0, TestItem.AddressA, source);
        bool later = PoisonedClearMemo.RequiresHistory(memo, 0, TestItem.AddressA, source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(threadA, Is.False, "precondition: A's probe ran before the publish");
            Assert.That(threadB, Is.True, "precondition: B's probe ran after the publish");
            Assert.That(later, Is.True, "an answer sampled before a publish must not be trusted after it");
        }
    }

    [Test]
    public void RequiresHistory_WithoutAPublish_ProbesOnce()
    {
        long[] memo = new long[1];
        RacingSource source = new() { Generation = 5 };

        PoisonedClearMemo.RequiresHistory(memo, 0, TestItem.AddressA, source);
        PoisonedClearMemo.RequiresHistory(memo, 0, TestItem.AddressA, source);

        Assert.That(source.Probes, Is.EqualTo(1), "a settled answer is reused until a capture publishes");
    }

    private sealed class RacingSource : IPoisonedClearSource
    {
        public long Generation { get; set; }
        public bool Poisoned { get; set; }
        public int Probes { get; private set; }
        public Action? DuringProbe { get; set; }

        public long ClearsGeneration => Generation;

        public bool HasCapturedSince(long generation) => Generation != generation;

        public bool RequiresHistory(Address address, bool slot)
        {
            Probes++;
            bool answer = Poisoned;
            Action? interleaved = DuringProbe;
            DuringProbe = null;
            interleaved?.Invoke();
            return answer;
        }
    }
}
