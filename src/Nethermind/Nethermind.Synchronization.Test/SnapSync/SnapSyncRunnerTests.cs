// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapSyncRunnerTests
{
    public enum DispatcherOutcome { Completes, Throws, Cancels }

    [TestCase(DispatcherOutcome.Completes, null)]
    [TestCase(DispatcherOutcome.Throws, typeof(InvalidOperationException))]
    [TestCase(DispatcherOutcome.Cancels, typeof(OperationCanceledException))]
    public async Task Run_invokes_lifecycle_in_order(DispatcherOutcome outcome, Type? expectedException)
    {
        List<string> calls = new();
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        factory.When(f => f.EnsureInitialize()).Do(_ => calls.Add("EnsureInitialize"));
        factory.When(f => f.FinalizeSync()).Do(_ => calls.Add("FinalizeSync"));

        using CancellationTokenSource cts = new();
        if (outcome == DispatcherOutcome.Cancels) cts.Cancel();

        SnapSyncRunner runner = new(token =>
        {
            calls.Add("dispatcher");
            return outcome switch
            {
                DispatcherOutcome.Throws => throw new InvalidOperationException("boom"),
                DispatcherOutcome.Cancels => throw new OperationCanceledException(token),
                _ => Task.CompletedTask,
            };
        }, factory);

        Func<Task> act = () => runner.Run(cts.Token);
        if (expectedException is null)
            await act.Should().NotThrowAsync();
        else
            await act.Should().ThrowAsync<Exception>().Where(e => expectedException.IsInstanceOfType(e));

        calls.Should().Equal("EnsureInitialize", "dispatcher", "FinalizeSync");
    }
}
