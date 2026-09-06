// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class ConcurrentDictionaryTests
{
    [Test]
    public void Locks()
    {
        ConcurrentDictionary<int, int> dictionary = new(new Dictionary<int, int> { { 0, 0 }, { 1, 1 }, { 2, 2 } });
        Task<int> updateTask;
        using (dictionary.AcquireLock())
        {
            updateTask = Task.Run(() => dictionary[3] = 3);
            Task.WaitAny(updateTask, Task.Delay(100));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(updateTask.IsCompleted, Is.False);
                Assert.That(dictionary.ContainsKey(3), Is.False);
            }
        }

        updateTask.Wait();
        Assert.That(dictionary.ContainsKey(3), Is.True);
    }

    [Test]
    public void NoResizeClear()
    {
        // Tests that the reflection works
        ConcurrentDictionary<int, int> dictionary = new(new Dictionary<int, int> { { 0, 0 }, { 1, 1 }, { 2, 2 } });
        dictionary.NoResizeClear();

        Assert.That(dictionary.Count, Is.EqualTo(0));
    }

    [Test]
    public void NoLockClear()
    {
        ConcurrentDictionary<int, int> dictionary = new(new Dictionary<int, int> { { 0, 0 }, { 1, 1 }, { 2, 2 } });

        Assert.That(dictionary.NoLockClear(), Is.True);
        Assert.That(dictionary, Is.Empty);
        // A cleared map must stay coherent for reuse: counts and lookups after re-population.
        Assert.That(dictionary.NoLockClear(), Is.False, "clearing an empty map is a no-op");

        for (int i = 0; i < 100; i++) dictionary[i] = i * 2;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dictionary.Count, Is.EqualTo(100));
            Assert.That(dictionary[42], Is.EqualTo(84));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void NoLockClearDoesNotTouchStripeLocks(CancellationToken testToken)
    {
        // The whole point of NoLockClear: it must make progress with every stripe lock held
        // elsewhere, for empty maps too - IsEmpty acquires all stripes to CONFIRM emptiness,
        // which is the sweep this helper exists to avoid.
        ConcurrentDictionary<int, int> populated = new(new Dictionary<int, int> { { 0, 0 }, { 1, 1 } });
        ConcurrentDictionary<int, int> empty = new();

        using (populated.AcquireLock())
        using (empty.AcquireLock())
        {
            // Another thread, because the stripe Monitors are reentrant on this one. The
            // assertion is completion while the stripes stay held; CancelAfter is only a
            // deadlock guard, not a wall-clock assertion.
            Task clearTask = Task.Run(() =>
            {
                Assert.That(populated.NoLockClear(), Is.True);
                Assert.That(empty.NoLockClear(), Is.False);
            });
            clearTask.Wait(testToken);
        }

        Assert.That(populated, Is.Empty);
    }
}
