// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
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
}
