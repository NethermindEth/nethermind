// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Events;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Events;

public class WaitForEventTests
{

    [Test]
    public void Test_WaitForEvent()
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

        Assert.That(() => awaitingEvent.IsCompleted, Is.False.After(100, 10));
        condCalled.Should().BeFalse();

        stubObj.TestEvent += Raise.Event<EventHandler<bool>>(this, false);

        condCalled.Should().BeTrue();
        Assert.That(() => awaitingEvent.IsCompleted, Is.False.After(100, 10));

        stubObj.TestEvent += Raise.Event<EventHandler<bool>>(this, true);
        Assert.That(() => awaitingEvent.IsCompleted, Is.True.After(100, 10));
    }


    [Test]
    public void Test_WaitForEvent_Cancelled()
    {
        ITestObj stubObj = Substitute.For<ITestObj>();

        CancellationTokenSource cts = new CancellationTokenSource();
        Task awaitingEvent = Wait.ForEventCondition<bool>(
            cts.Token,
            (e) => stubObj.TestEvent += e,
            (e) => stubObj.TestEvent -= e,
            (cond) => cond);

        Assert.That(() => awaitingEvent.IsCompleted, Is.False.After(100, 10));

        cts.Cancel();

        Assert.That(() => awaitingEvent.IsCanceled, Is.True.After(100, 10));
    }

    public interface ITestObj
    {
        public event EventHandler<bool> TestEvent;
    }
}
