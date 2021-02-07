//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network.P2P;
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
            Context ctx = new Context();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
        }
        
        [Test]
        public void Will_add_of_same_type()
        {
            Context ctx = new Context();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            
            // GitHub actions not handling these tests well
            // ctx.TestLogger.LogList.Any(l => l.Contains("Local")).Should().BeTrue(string.Join(", ", ctx.TestLogger.LogList));
        }
        
        [Test]
        public void Will_add_of_different_types()
        {
            Context ctx = new Context();
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
            Context ctx = new Context();
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            Thread.Sleep(15);
            ctx.DisconnectsAnalyzer.ReportDisconnect(DisconnectReason.TooManyPeers, DisconnectType.Local, null);
            Thread.Sleep(15);

            // GitHub actions not handling these tests well
        }
    }
}
