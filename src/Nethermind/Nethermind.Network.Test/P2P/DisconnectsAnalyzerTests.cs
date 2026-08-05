// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using Nethermind.Logging;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class DisconnectsAnalyzerTests
    {
        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        private sealed class Context : IDisposable
        {
            private readonly FlushCapturingLogger _logger = new();

            public DisconnectsAnalyzer DisconnectsAnalyzer { get; }

            public Context()
            {
                ILogManager logManager = Substitute.For<ILogManager>();
                logManager.GetClassLogger<DisconnectsAnalyzer>().Returns(new ILogger(_logger));
                // The default 10s interval keeps the first flush far away, so reports recorded
                // before TriggerFlushes cannot straddle a flush boundary.
                DisconnectsAnalyzer = new DisconnectsAnalyzer(logManager);
            }

            public void TriggerFlushes() => DisconnectsAnalyzer.WithIntervalOverride(10);

            public void ShouldEventuallyReport(string pattern, int times = 1) =>
                Assert.That(PollUntil(() => _logger.CountMatches(pattern) >= times), Is.True,
                    () => $"expected {times} flush report(s) matching '{pattern}'; got: {_logger.Dump()}");

            public void ShouldStayAt(string pattern, int times)
            {
                // The analyzer double-buffers its counters, so a lost clear resurfaces stale
                // categories in every other flush; watch a few more flushes to rule that out.
                int flushesSeen = _logger.FlushCount;
                Assert.That(PollUntil(() => _logger.FlushCount >= flushesSeen + 4), Is.True,
                    "flush timer stopped ticking");
                Assert.That(_logger.CountMatches(pattern), Is.EqualTo(times),
                    () => $"a cleared category resurfaced in a later flush; got: {_logger.Dump()}");
            }

            public void Dispose() => DisconnectsAnalyzer.Dispose();

            private static bool PollUntil(Func<bool> condition)
            {
                long deadline = Environment.TickCount64 + (long)WaitLimit.TotalMilliseconds;
                while (Environment.TickCount64 < deadline)
                {
                    if (condition()) return true;
                    Thread.Sleep(1);
                }

                return condition();
            }

            private sealed class FlushCapturingLogger : InterfaceLogger
            {
                private readonly ConcurrentQueue<string> _flushes = new();

                public int FlushCount => _flushes.Count;

                // Assertions match the report's column format (Type PadRight(8), Reason
                // PadRight(24), count PadLeft(4)); a format change in DisconnectsAnalyzer
                // surfaces here as a wait timeout.
                public int CountMatches(string pattern)
                {
                    int count = 0;
                    foreach (string flush in _flushes)
                    {
                        if (Regex.IsMatch(flush, pattern)) count++;
                    }

                    return count;
                }

                public string Dump() => string.Join(" | ", _flushes);

                public void Info(string text) => _flushes.Enqueue(text);
                public void Warn(string text) { }
                public void Debug(string text) { }
                public void Trace(string text) { }
                public void Error(string text, Exception? ex = null) { }

                public bool IsInfo => true;
                public bool IsWarn => true;
                public bool IsDebug => true;
                public bool IsTrace => true;
                public bool IsError => true;
            }
        }

        [Test]
        public void Can_pass_null_details()
        {
            using Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.TriggerFlushes();

            ctx.ShouldEventuallyReport(@"Local\s+TooManyPeers\s+1\b");
        }

        [Test]
        public void Will_add_of_same_type()
        {
            using Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.TriggerFlushes();

            ctx.ShouldEventuallyReport(@"Local\s+TooManyPeers\s+2\b");
        }

        [Test]
        public void Will_add_of_different_types()
        {
            using Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Remote, null);
            ctx.TriggerFlushes();

            ctx.ShouldEventuallyReport(@"Local\s+TooManyPeers\s+1\b");
            ctx.ShouldEventuallyReport(@"Remote\s+TooManyPeers\s+1\b");
        }

        [Test]
        public void Will_clear_after_report()
        {
            using Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.TriggerFlushes();
            ctx.ShouldEventuallyReport(@"Local\s+TooManyPeers\s+1\b");

            // No second report is issued (it could race the flush's enumerate-then-clear
            // window): a lost clear is observable on its own, because the stale count
            // resurfaces in later flushes - the category must appear exactly once.
            ctx.ShouldStayAt(@"Local\s+TooManyPeers\s+1\b", times: 1);
        }
    }
}
