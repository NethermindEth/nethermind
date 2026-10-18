// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Db.Rocks;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class PrefetchGateTests
{
    [Test]
    public void New_gate_prefetches()
    {
        PrefetchGate gate = new();
        Assert.That(Enumerable.Range(0, 64).All(_ => gate.ShouldPrefetch()), Is.True);
    }

    [Test]
    public void Cached_batches_close_the_gate_except_for_periodic_probes()
    {
        PrefetchGate gate = new();
        for (int i = 0; i < 32; i++) gate.Record(cold: false);

        int sent = Enumerable.Range(0, PrefetchGate.ProbeInterval * 8).Count(_ => gate.ShouldPrefetch());

        Assert.That(sent, Is.EqualTo(8));
    }

    [Test]
    public void Cold_batches_reopen_a_closed_gate()
    {
        PrefetchGate gate = new();
        for (int i = 0; i < 32; i++) gate.Record(cold: false);
        for (int i = 0; i < 8; i++) gate.Record(cold: true);

        Assert.That(Enumerable.Range(0, 16).All(_ => gate.ShouldPrefetch()), Is.True);
    }

    [Test]
    public void A_single_cold_batch_among_cached_ones_keeps_the_gate_closed()
    {
        PrefetchGate gate = new();
        for (int i = 0; i < 32; i++) gate.Record(cold: false);
        gate.Record(cold: true);
        for (int i = 0; i < 8; i++) gate.Record(cold: false);

        int sent = Enumerable.Range(0, PrefetchGate.ProbeInterval).Count(_ => gate.ShouldPrefetch());

        Assert.That(sent, Is.EqualTo(1));
    }
}
