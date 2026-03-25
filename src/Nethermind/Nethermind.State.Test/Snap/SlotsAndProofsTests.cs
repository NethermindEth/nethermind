// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.State.Test.Snap;

/// <summary>
/// Regression tests for SlotsAndProofs disposal thread safety.
/// The double-dispose occurs because SnapProvider.AddStorageRange disposes the response,
/// then SnapSyncFeed.HandleResponse's finally block disposes the batch (which disposes
/// the response again). Additionally, MessageDictionary.CleanOldRequests can race from
/// a background thread.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class SlotsAndProofsTests
{
    [Test]
    public void Concurrent_dispose_calls_dispose_inner_resources_exactly_once()
    {
        int disposeCount = 0;
        TrackingOwnedList outerList = new(() => Interlocked.Increment(ref disposeCount));

        SlotsAndProofs sut = new()
        {
            PathsAndSlots = outerList,
            Proofs = null
        };

        // Synchronize both threads to enter Dispose as close together as possible,
        // simulating the response handler + timeout cleanup race
        ManualResetEventSlim gate = new(false);
        Task t1 = Task.Run(() => { gate.Wait(); sut.Dispose(); });
        Task t2 = Task.Run(() => { gate.Wait(); sut.Dispose(); });

        gate.Set();
        Task.WaitAll(t1, t2);

        Assert.That(disposeCount, Is.EqualTo(1),
            "DisposeRecursive must run exactly once even under concurrent Dispose() calls. " +
            "A value > 1 means ArrayPool buffers are returned multiple times, corrupting the pool.");
    }

    [Test]
    public void Dispose_is_idempotent()
    {
        int disposeCount = 0;
        TrackingOwnedList outerList = new(() => Interlocked.Increment(ref disposeCount));

        SlotsAndProofs sut = new()
        {
            PathsAndSlots = outerList,
            Proofs = null
        };

        // Same-thread double-dispose (the SnapProvider + SnapSyncBatch path)
        sut.Dispose();
        sut.Dispose();
        sut.Dispose();

        Assert.That(disposeCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Minimal IOwnedReadOnlyList that counts Dispose calls.
    /// DisposeRecursive iterates elements (none here) then calls Dispose on the list.
    /// </summary>
    private sealed class TrackingOwnedList(Action onDispose) : IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>
    {
        public IOwnedReadOnlyList<PathWithStorageSlot> this[int index] => throw new NotImplementedException();
        public int Count => 0;
        public ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> AsSpan() => ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty;
        public System.Collections.Generic.IEnumerator<IOwnedReadOnlyList<PathWithStorageSlot>> GetEnumerator()
        {
            yield break;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => onDispose();
    }
}
