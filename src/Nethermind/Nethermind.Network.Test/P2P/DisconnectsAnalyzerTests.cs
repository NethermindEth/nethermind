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

            public void ShouldEventuallyReport(string pattern, int times = 1)
            {
                bool Matches() => _logger.CountMatches(pattern) >= times;
                Assert.That(SpinWait.SpinUntil(Matches, TimeSpan.FromSeconds(10)), Is.True,
                    () => $"expected {times} flush report(s) matching '{pattern}'; got: {_logger.Dump()}");
            }

            public void ShouldNeverHaveReported(string pattern) =>
                Assert.That(_logger.CountMatches(pattern), Is.Zero,
                    () => $"unexpected flush report matching '{pattern}'; got: {_logger.Dump()}");

            public void ShouldStayAt(string pattern, int times)
            {
                // The analyzer double-buffers its counters, so a lost clear resurfaces stale
                // categories in every other flush; watch a few more flushes to rule that out.
                int flushesSeen = _logger.FlushCount;
                Assert.That(SpinWait.SpinUntil(() => _logger.FlushCount >= flushesSeen + 4, TimeSpan.FromSeconds(10)),
                    Is.True, "flush timer stopped ticking");
                Assert.That(_logger.CountMatches(pattern), Is.EqualTo(times),
                    () => $"a cleared category resurfaced in a later flush; got: {_logger.Dump()}");
            }

            public void Dispose() => DisconnectsAnalyzer.Dispose();

            private sealed class FlushCapturingLogger : InterfaceLogger
            {
                private readonly ConcurrentQueue<string> _flushes = new();

                public int FlushCount => _flushes.Count;

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

            // The first report was flushed and cleared above, so this one must show up as
            // a fresh count of 1 in a later flush - a 2 means the clear was lost.
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.ShouldEventuallyReport(@"Local\s+TooManyPeers\s+1\b", times: 2);
            ctx.ShouldNeverHaveReported(@"Local\s+TooManyPeers\s+2\b");
            ctx.ShouldStayAt(@"Local\s+TooManyPeers\s+1\b", times: 2);
        }
    }
}
