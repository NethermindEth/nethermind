// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#nullable enable

using System.Collections.Generic;
using Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing.BlockStm;

public class MultiVersionMemoryTests
{
    private static MultiVersionMemory NewMemory(int txCount) => new(txCount);

    private static ParallelStateKey K(int i) => ParallelStateKey.ForAccount(TestItem.Addresses[i]);

    private static Dictionary<ParallelStateKey, object> WriteSet(params (ParallelStateKey K, object V)[] entries)
    {
        Dictionary<ParallelStateKey, object> d = [];
        foreach ((ParallelStateKey k, object v) in entries) d[k] = v;
        return d;
    }

    [Test]
    public void First_incarnation_with_writes_signals_change()
    {
        MultiVersionMemory mem = NewMemory(2);
        bool changed = mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v")));
        Assert.That(changed, Is.True);
    }

    [Test]
    public void Empty_writeset_signals_no_change()
    {
        MultiVersionMemory mem = NewMemory(2);
        bool changed = mem.Record(new TxVersion(0, 0), [], WriteSet());
        Assert.That(changed, Is.False);
    }

    // Bug 1 regression: previously wroteNewLocation only fired on new keys. A second
    // incarnation that rewrote the same keys returned false, so the scheduler never
    // lowered _validationIndex and higher already-validated txs kept their stale reads.
    [Test]
    public void Re_executing_same_keys_signals_change()
    {
        MultiVersionMemory mem = NewMemory(2);
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v1")));
        bool changed = mem.Record(new TxVersion(0, 1), [], WriteSet((K(0), (object)"v2")));
        Assert.That(changed, Is.True, "re-write at higher incarnation must invalidate higher reads");
    }

    // Bug 1 regression: the original implementation removed deleted keys silently —
    // wroteNewLocation stayed false, so higher txs that read the dropped key kept a
    // dangling reference to an incarnation no longer in the data dict.
    [Test]
    public void Removed_key_signals_change()
    {
        MultiVersionMemory mem = NewMemory(2);
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v"), (K(1), (object)"w")));
        bool changed = mem.Record(new TxVersion(0, 1), [], WriteSet((K(0), (object)"v")));
        Assert.That(changed, Is.True, "dropping a previously-written key must invalidate higher reads");
    }

    [Test]
    public void Newly_added_key_signals_change()
    {
        MultiVersionMemory mem = NewMemory(2);
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v")));
        bool changed = mem.Record(new TxVersion(0, 1), [], WriteSet((K(0), (object)"v"), (K(1), (object)"w")));
        Assert.That(changed, Is.True);
    }

    [Test]
    public void TryRead_returns_NotFound_for_unwritten_location()
    {
        MultiVersionMemory mem = NewMemory(2);
        Status status = mem.TryRead(K(0), 1, out TxVersion version, out _);
        Assert.That(status, Is.EqualTo(Status.NotFound));
        Assert.That(version.IsEmpty, Is.True);
    }

    [Test]
    public void TryRead_returns_latest_lower_write_with_its_version()
    {
        MultiVersionMemory mem = NewMemory(3);
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v0")));
        mem.Record(new TxVersion(1, 0), [], WriteSet((K(0), (object)"v1")));

        Status status = mem.TryRead(K(0), 2, out TxVersion version, out object? value);

        Assert.That(status, Is.EqualTo(Status.Ok));
        Assert.That(version, Is.EqualTo(new TxVersion(1, 0)));
        Assert.That(value, Is.EqualTo("v1"));
    }

    [Test]
    public void TryRead_skips_own_version()
    {
        MultiVersionMemory mem = NewMemory(2);
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v0")));

        Status status = mem.TryRead(K(0), 0, out _, out _);
        Assert.That(status, Is.EqualTo(Status.NotFound));
    }

    [Test]
    public void ConvertWritesToEstimates_makes_TryRead_return_ReadError()
    {
        MultiVersionMemory mem = NewMemory(2);
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v0")));
        mem.ConvertWritesToEstimates(0);

        Status status = mem.TryRead(K(0), 1, out _, out _);
        Assert.That(status, Is.EqualTo(Status.ReadError));
    }

    [Test]
    public void ValidateReadSet_passes_when_no_lower_tx_changed()
    {
        MultiVersionMemory mem = NewMemory(2);
        // tx 1 read tx 0's value at version (0, 0)
        HashSet<Read> readSet = [new Read(K(0), new TxVersion(0, 0))];
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v0")));
        mem.Record(new TxVersion(1, 0), readSet, WriteSet());

        Assert.That(mem.ValidateReadSet(1), Is.True);
    }

    [Test]
    public void ValidateReadSet_fails_when_lower_tx_re_executed()
    {
        MultiVersionMemory mem = NewMemory(2);
        HashSet<Read> readSet = [new Read(K(0), new TxVersion(0, 0))];
        mem.Record(new TxVersion(0, 0), [], WriteSet((K(0), (object)"v0")));
        mem.Record(new TxVersion(1, 0), readSet, WriteSet());
        // re-execute tx 0 → new incarnation → tx 1's read version is now stale
        mem.Record(new TxVersion(0, 1), [], WriteSet((K(0), (object)"v0_new")));

        Assert.That(mem.ValidateReadSet(1), Is.False);
    }
}
