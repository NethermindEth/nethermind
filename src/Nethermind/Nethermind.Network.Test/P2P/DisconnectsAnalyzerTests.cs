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
                // The constructor arms the flush timer at 10 s. Reports recorded before
                // TriggerFlushes() shortens the interval cannot race a flush.
                DisconnectsAnalyzer = new DisconnectsAnalyzer(logManager);
            }

            public void TriggerFlushes() => DisconnectsAnalyzer.WithIntervalOverride(10);

            public void ShouldEventuallyReport(string pattern)
            {
                // A flush can land in SpinUntil's final sleep tick. Re-check once after a timeout.
                Func<bool> reported = () => _logger.CountMatches(pattern) >= 1;
                Assert.That(SpinWait.SpinUntil(reported, WaitLimit) || reported(), Is.True,
                    () => $"expected a flush report matching '{pattern}'; got: {_logger.Dump()}");
            }

            public void ShouldStayAt(string pattern, int times)
            {
                // The analyzer double-buffers its counters. A lost clear makes a stale
                // category appear again in every other flush. Watch more flushes to detect that.
                int flushesSeen = _logger.FlushCount;
                Func<bool> flushed = () => _logger.FlushCount >= flushesSeen + 4;
                Assert.That(SpinWait.SpinUntil(flushed, WaitLimit) || flushed(), Is.True,
                    "flush timer stopped ticking");
                Assert.That(_logger.CountMatches(pattern), Is.EqualTo(times),
                    () => $"a cleared category resurfaced in a later flush; got: {_logger.Dump()}");
            }

            public void Dispose() => DisconnectsAnalyzer.Dispose();

            private sealed class FlushCapturingLogger : InterfaceLogger
            {
                private readonly ConcurrentQueue<string> _flushes = new();

                public int FlushCount => _flushes.Count;

                // Assertions match the report's column format: Type PadRight(8), Reason
                // PadRight(24), count PadLeft(4). A format change in DisconnectsAnalyzer
                // causes a wait timeout here.
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

            // Do not send a second report: it can race the flush's enumerate-then-clear
            // window. A lost clear is visible on its own, because the stale count appears
            // again in later flushes. The category must appear exactly one time.
            ctx.ShouldStayAt(@"Local\s+TooManyPeers\s+1\b", times: 1);
        }
    }
}
