// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Store.Test.Snap;

[Parallelizable(ParallelScope.Self)]
public class SlotsAndProofsTests
{
    private static (SlotsAndProofs Sut, Func<int> GetDisposeCount) CreateWithTracking()
    {
        int disposeCount = 0;
        TrackingOwnedList list = new(() => Interlocked.Increment(ref disposeCount));
        SlotsAndProofs sut = new() { PathsAndSlots = list, Proofs = null };
        return (sut, () => disposeCount);
    }

    [Test]
    public void Concurrent_dispose_calls_dispose_inner_resources_exactly_once()
    {
        (SlotsAndProofs sut, Func<int> getCount) = CreateWithTracking();

        ManualResetEventSlim gate = new(false);
        Task t1 = Task.Run(() => { gate.Wait(); sut.Dispose(); });
        Task t2 = Task.Run(() => { gate.Wait(); sut.Dispose(); });
        gate.Set();
        Task.WaitAll(t1, t2);

        Assert.That(getCount(), Is.EqualTo(1),
            "DisposeRecursive must run exactly once — a value > 1 means ArrayPool buffers are returned multiple times");
    }

    [Test]
    public void Dispose_is_idempotent()
    {
        (SlotsAndProofs sut, Func<int> getCount) = CreateWithTracking();

        sut.Dispose();
        sut.Dispose();
        sut.Dispose();

        Assert.That(getCount(), Is.EqualTo(1));
    }

    [Test]
    public void Dispose_with_null_fields_does_not_throw()
    {
        SlotsAndProofs sut = new() { PathsAndSlots = null!, Proofs = null };
        Assert.DoesNotThrow(sut.Dispose);
    }

    private sealed class TrackingOwnedList(Action onDispose) : IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>
    {
        public IOwnedReadOnlyList<PathWithStorageSlot> this[int index] => throw new NotImplementedException();
        public int Count => 0;
        public ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> AsSpan() => ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty;
        public System.Collections.Generic.IEnumerator<IOwnedReadOnlyList<PathWithStorageSlot>> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => onDispose();
    }
}
