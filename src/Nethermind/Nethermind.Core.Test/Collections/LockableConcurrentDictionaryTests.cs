using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class LockableConcurrentDictionaryTests
{
    [Test]
    public void X()
    {
        ConcurrentDictionary<int, int> dictionary = new(new Dictionary<int, int> { { 0, 0 }, { 1, 1 }, { 2, 2 } });
        Task<int> updateTask;
        using (var locker = ConcurrentDictionaryLock<int, int>.Acquire(dictionary))
        {
            updateTask = Task.Run(() => dictionary[3] = 3);
            Task.WaitAny(updateTask, Task.Delay(100));
            updateTask.IsCompleted.Should().BeFalse();
            dictionary.ContainsKey(3).Should().BeFalse();
        }

        updateTask.Wait();
        dictionary.ContainsKey(3).Should().BeTrue();
    }
}
