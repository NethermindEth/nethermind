// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class VerifyTrieStarterTests
{
    [Test]
    public void Logs_cancellation_not_error_when_verify_trie_is_cancelled_as_aggregate()
    {
        // The parallel stats walk surfaces shutdown cancellation as an AggregateException of
        // TaskCanceledExceptions; it must be reported as a cancellation, never as an error.
        CapturingLogManager logManager = RunVerifyTrieThatThrows(
            new AggregateException(new TaskCanceledException(), new TaskCanceledException()));

        Assert.That(() => logManager.Infos, Has.Some.Contains("Verify trie cancelled").After(5000, 20));
        Assert.That(logManager.Errors, Is.Empty);
    }

    [Test]
    public void Logs_error_when_a_real_fault_is_aggregated_with_cancellations()
    {
        // A genuine failure alongside cancellations leaves a non-cancellation leaf and must
        // still surface as an error, so a real divergence can never be silently swallowed.
        CapturingLogManager logManager = RunVerifyTrieThatThrows(
            new AggregateException(new TaskCanceledException(), new InvalidOperationException("boom")));

        Assert.That(() => logManager.Errors, Has.Some.Contains("Error in verify trie").After(5000, 20));
    }

    private static CapturingLogManager RunVerifyTrieThatThrows(Exception toThrow)
    {
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager
            .VerifyTrie(Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw toThrow);

        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(CancellationToken.None);

        CapturingLogManager logManager = new();
        VerifyTrieStarter starter = new(worldStateManager, exitSource, logManager);

        Assert.That(starter.TryStartVerifyTrie(Build.A.BlockHeader.TestObject), Is.True);
        return logManager;
    }

    private sealed class CapturingLogManager : ILogManager
    {
        public ConcurrentQueue<string> Infos { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();

        public ILogger GetClassLogger<T>() => new(new Logger(this));
        public ILogger GetLogger(string loggerName) => new(new Logger(this));

        private sealed class Logger(CapturingLogManager parent) : InterfaceLogger
        {
            public void Info(string text) => parent.Infos.Enqueue(text);
            public void Warn(string text) { }
            public void Debug(string text) { }
            public void Trace(string text) { }
            public void Error(string text, Exception? ex = null) => parent.Errors.Enqueue(text);
            public bool IsInfo => true;
            public bool IsWarn => true;
            public bool IsDebug => true;
            public bool IsTrace => true;
            public bool IsError => true;
        }
    }
}
