// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
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
            Assert.That(() => updateTask.IsCompleted, Is.False.After(100, 10));
            dictionary.ContainsKey(3).Should().BeFalse();
        }

        updateTask.Wait();
        dictionary.ContainsKey(3).Should().BeTrue();
    }

    [Test]
    public void NoResizeClear()
    {
        // Tests that the reflection works
        ConcurrentDictionary<int, int> dictionary = new(new Dictionary<int, int> { { 0, 0 }, { 1, 1 }, { 2, 2 } });
        dictionary.NoResizeClear();

        dictionary.Count.Should().Be(0);
    }
}
