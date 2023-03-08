// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class SpanConcurrentDictionaryTests
{
    [Test]
    public void Locks()
    {
        SpanConcurrentDictionary<byte, int> dictionary = new(
            new Dictionary<byte[], int>
            {
                { new byte[] { 0 }, 0 },
                { new byte[] { 1 }, 1 },
                { new byte[] { 2 }, 2 }
            },
            Bytes.SpanEqualityComparer);

        Task<int> updateTask;
        byte[] key3 = { 3 };
        using (dictionary.AcquireLock())
        {
            updateTask = Task.Run(() => dictionary[key3] = 3);
            Task.WaitAny(updateTask, Task.Delay(100));
            updateTask.IsCompleted.Should().BeFalse();
            dictionary.ContainsKey(key3).Should().BeFalse();
        }

        updateTask.Wait();
        dictionary.ContainsKey(key3).Should().BeTrue();
    }
}
