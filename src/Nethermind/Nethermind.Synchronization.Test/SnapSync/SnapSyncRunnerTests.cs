// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapSyncRunnerTests
{
    [Test]
    public async Task Run_calls_EnsureInitialize_then_dispatcher_then_FinalizeSync_in_order()
    {
        List<string> calls = new();
        ISnapTrieFactory factory = MakeRecordingFactory(calls);

        SnapSyncRunner runner = new(token =>
        {
            calls.Add("dispatcher");
            return Task.CompletedTask;
        }, factory);

        await runner.Run(CancellationToken.None);

        calls.Should().Equal("EnsureInitialize", "dispatcher", "FinalizeSync");
    }

    [Test]
    public async Task Run_calls_FinalizeSync_when_dispatcher_throws()
    {
        List<string> calls = new();
        ISnapTrieFactory factory = MakeRecordingFactory(calls);

        SnapSyncRunner runner = new(_ =>
        {
            calls.Add("dispatcher");
            throw new InvalidOperationException("boom");
        }, factory);

        Func<Task> act = () => runner.Run(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        calls.Should().Equal("EnsureInitialize", "dispatcher", "FinalizeSync");
    }

    [Test]
    public async Task Run_calls_FinalizeSync_when_dispatcher_cancelled()
    {
        List<string> calls = new();
        ISnapTrieFactory factory = MakeRecordingFactory(calls);

        using CancellationTokenSource cts = new();
        cts.Cancel();
        SnapSyncRunner runner = new(token =>
        {
            calls.Add("dispatcher");
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, factory);

        Func<Task> act = () => runner.Run(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        calls.Should().Equal("EnsureInitialize", "dispatcher", "FinalizeSync");
    }

    private static ISnapTrieFactory MakeRecordingFactory(List<string> calls)
    {
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        factory.When(f => f.EnsureInitialize()).Do(_ => calls.Add("EnsureInitialize"));
        factory.When(f => f.FinalizeSync()).Do(_ => calls.Add("FinalizeSync"));
        return factory;
    }
}
