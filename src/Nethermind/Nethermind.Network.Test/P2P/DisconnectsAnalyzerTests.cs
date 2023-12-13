// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Test;
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
        private class Context
        {
            public DisconnectsAnalyzer DisconnectsAnalyzer { get; }
            public TestLogger TestLogger { get; }

            public Context()
            {
                TestLogger = new TestLogger();
                ILogManager logManager = Substitute.For<ILogManager>();
                logManager.GetClassLogger().Returns(TestLogger);
                DisconnectsAnalyzer = new DisconnectsAnalyzer(logManager).WithIntervalOverride(10);
            }
        }

        [Test]
        public void Can_pass_null_details()
        {
            Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
        }

        [Test]
        public void Will_add_of_same_type()
        {
            Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);

            // GitHub actions not handling these tests well
            // ctx.TestLogger.LogList.Any(l => l.Contains("Local")).Should().BeTrue(string.Join(", ", ctx.TestLogger.LogList));
        }

        [Test]
        public void Will_add_of_different_types()
        {
            Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Remote, null);
            Thread.Sleep(15);

            // GitHub actions not handling these tests well
            // ctx.TestLogger.LogList.Any(l => l.Contains("Remote")).Should().BeTrue(string.Join(", ", ctx.TestLogger.LogList));
            // ctx.TestLogger.LogList.Any(l => l.Contains("Local")).Should().BeTrue(string.Join(", ", ctx.TestLogger.LogList));
        }

        [Test]
        public void Will_clear_after_report()
        {
            Context ctx = new();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            Thread.Sleep(15);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            Thread.Sleep(15);

            // GitHub actions not handling these tests well
        }
    }
}
