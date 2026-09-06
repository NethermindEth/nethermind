// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Events;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Events;

public class WaitForEventTests
{

    [Test]
    public async Task Test_WaitForEvent()
    {
        ITestObj stubObj = Substitute.For<ITestObj>();

        bool condCalled = false;

        Task awaitingEvent = Wait.ForEventCondition<bool>(
            CancellationToken.None,
            (e) => stubObj.TestEvent += e,
            (e) => stubObj.TestEvent -= e,
            (cond) =>
            {
                condCalled = true;
                return cond;
            });

        await Task.Delay(100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(awaitingEvent.IsCompleted, Is.False);
            Assert.That(condCalled, Is.False);
        }

        stubObj.TestEvent += Raise.Event<EventHandler<bool>>(this, false);

        Assert.That(condCalled, Is.True);
        await Task.Delay(100);
        Assert.That(awaitingEvent.IsCompleted, Is.False);

        stubObj.TestEvent += Raise.Event<EventHandler<bool>>(this, true);
        await Task.Delay(100);
        Assert.That(awaitingEvent.IsCompleted, Is.True);
    }


    [Test]
    public async Task Test_WaitForEvent_Cancelled()
    {
        ITestObj stubObj = Substitute.For<ITestObj>();

        CancellationTokenSource cts = new();
        Task awaitingEvent = Wait.ForEventCondition<bool>(
            cts.Token,
            (e) => stubObj.TestEvent += e,
            (e) => stubObj.TestEvent -= e,
            (cond) => cond);

        await Task.Delay(100);
        Assert.That(awaitingEvent.IsCompleted, Is.False);

        cts.Cancel();

        await Task.Delay(100);
        Assert.That(awaitingEvent.IsCanceled, Is.True);
    }

    public interface ITestObj
    {
        public event EventHandler<bool> TestEvent;
    }
}
