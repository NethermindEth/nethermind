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
        awaitingEvent.IsCompleted.Should().BeFalse();
        condCalled.Should().BeFalse();

        stubObj.TestEvent += Raise.Event<EventHandler<bool>>(this, false);

        condCalled.Should().BeTrue();
        await Task.Delay(100);
        awaitingEvent.IsCompleted.Should().BeFalse();

        stubObj.TestEvent += Raise.Event<EventHandler<bool>>(this, true);
        await Task.Delay(100);
        awaitingEvent.IsCompleted.Should().BeTrue();
    }


    [Test]
    public async Task Test_WaitForEvent_Cancelled()
    {
        ITestObj stubObj = Substitute.For<ITestObj>();

        CancellationTokenSource cts = new CancellationTokenSource();
        Task awaitingEvent = Wait.ForEventCondition<bool>(
            cts.Token,
            (e) => stubObj.TestEvent += e,
            (e) => stubObj.TestEvent -= e,
            (cond) => cond);

        await Task.Delay(100);
        awaitingEvent.IsCompleted.Should().BeFalse();

        cts.Cancel();

        await Task.Delay(100);
        awaitingEvent.IsCanceled.Should().BeTrue();
    }

    public interface ITestObj
    {
        public event EventHandler<bool> TestEvent;
    }
}
