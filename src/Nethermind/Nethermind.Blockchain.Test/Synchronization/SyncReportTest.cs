/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class SyncReportTest
    {
        [Test]
        public void Smoke()
        {
            SyncReport syncReport = new SyncReport(Substitute.For<IEthSyncPeerPool>(), Substitute.For<INodeStatsManager>(), new SyncConfig(), LimboLogs.Instance, 10);
            Thread.Sleep(20);
            syncReport.CurrentSyncMode = SyncMode.Headers;
            Thread.Sleep(20);
            syncReport.CurrentSyncMode = SyncMode.Full;
            Thread.Sleep(20);
            syncReport.CurrentSyncMode = SyncMode.FastBlocks;
            Thread.Sleep(20);
            syncReport.CurrentSyncMode = SyncMode.StateNodes;
            Thread.Sleep(20);
            syncReport.CurrentSyncMode = SyncMode.WaitForProcessor;
            Thread.Sleep(20);
            syncReport.FastBlocksHeaders.MarkEnd();
            syncReport.FastBlocksBodies.MarkEnd();
            syncReport.FastBlocksReceipts.MarkEnd();
            syncReport.CurrentSyncMode = SyncMode.WaitForProcessor;
            Thread.Sleep(20);
        }
    }
}