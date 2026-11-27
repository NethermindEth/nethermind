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
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class VerifyTrieStarterTests
{
    // A failsafe bound for waits on the background run; a passing run needs a few milliseconds.
    private const int FailsafeMs = 10_000;
    private const int PollMs = 10;

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
        Assert.That(logManager.Infos, Has.None.Contains("Verify trie cancelled"));
    }

    [Test]
    public void Logs_error_when_cancelled_without_process_exit()
    {
        // A cancellation that did not originate from process exit is unexpected: the sweep never
        // finished, so it must surface as an error rather than a benign "Verify trie cancelled".
        CapturingLogManager logManager = RunVerifyTrieThatThrows(
            new OperationCanceledException(), processExiting: false);

        Assert.That(() => logManager.Errors, Has.Some.Contains("Error in verify trie").After(5000, 20));
        Assert.That(logManager.Infos, Has.None.Contains("Verify trie cancelled"));
    }

    [Test]
    public void Starts_again_after_a_verification_completes([Values] bool verified)
    {
        int calls = 0;
        VerifyTrieStarter starter = CreateStarter(_ =>
        {
            Interlocked.Increment(ref calls);
            return verified;
        });
        BlockHeader header = Build.A.BlockHeader.TestObject;

        Assert.That(starter.TryStartVerifyTrie(header), Is.True);
        Assert.That(() => starter.TryStartVerifyTrie(header), Is.True.After(FailsafeMs, PollMs));
        Assert.That(() => Volatile.Read(ref calls), Is.EqualTo(2).After(FailsafeMs, PollMs));
    }

    // A cancellation, alone or aggregated as the parallel walk throws it, takes the error path without process exit
    // and the cancellation path with it.
    [Test]
    public void Starts_again_after_a_verification_throws([Values] bool processExiting, [Values] bool aggregated)
    {
        Exception exception = aggregated ? new AggregateException(new OperationCanceledException()) : new OperationCanceledException();
        VerifyTrieStarter starter = CreateStarter(_ => throw exception, processExiting: processExiting);
        BlockHeader header = Build.A.BlockHeader.TestObject;

        Assert.That(starter.TryStartVerifyTrie(header), Is.True);
        Assert.That(() => starter.TryStartVerifyTrie(header), Is.True.After(FailsafeMs, PollMs));
    }

    [Test]
    public async Task Refuses_a_start_while_a_verification_runs_and_accepts_one_after_it_ends()
    {
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The held run ends only when the test opens the gate; the failsafe bounds the test's own waits.
        VerifyTrieStarter starter = CreateStarter(_ =>
        {
            running.TrySetResult();
            gate.Task.Wait();
            return true;
        });
        BlockHeader header = Build.A.BlockHeader.TestObject;

        try
        {
            // Both starts run on pool threads, so a run they admit goes to the default scheduler and never runs inline on
            // the test thread, where it would wait on the gate before the finally could open it.
            Assert.That(await Task.Run(() => starter.TryStartVerifyTrie(header)), Is.True);
            await running.Task.WaitAsync(TimeSpan.FromMilliseconds(FailsafeMs));
            Assert.That(await Task.Run(() => starter.TryStartVerifyTrie(header)), Is.False, "a second verification started while the first one runs");
        }
        finally
        {
            gate.SetResult();
        }

        Assert.That(() => starter.TryStartVerifyTrie(header), Is.True.After(FailsafeMs, PollMs));
    }

    private static CapturingLogManager RunVerifyTrieThatThrows(Exception toThrow, bool processExiting = true)
    {
        CapturingLogManager logManager = new();
        VerifyTrieStarter starter = CreateStarter(_ => throw toThrow, logManager, processExiting);

        Assert.That(starter.TryStartVerifyTrie(Build.A.BlockHeader.TestObject), Is.True);
        return logManager;
    }

    private static VerifyTrieStarter CreateStarter(Func<CallInfo, bool> verifyTrie, ILogManager? logManager = null, bool processExiting = false)
    {
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager
            .VerifyTrie(Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns(verifyTrie);

        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(new CancellationToken(processExiting));

        return new VerifyTrieStarter(worldStateManager, exitSource, logManager ?? LimboLogs.Instance);
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
