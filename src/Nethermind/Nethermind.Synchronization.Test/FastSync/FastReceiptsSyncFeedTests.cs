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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    public class FastReceiptsSyncFeedTests
    {
        private ISyncModeSelector _selector;
        private IReceiptStorage _receiptStorage;
        private ISpecProvider _specProvider;
        private IBlockTree _blockTree;
        private ISyncPeerPool _syncPeerPool;
        private ISyncConfig _syncConfig;
        private ISyncReport _syncReport;
        private FastReceiptsSyncFeed _feed;

        [Test]
        public void Test()
        {
            _selector = Substitute.For<ISyncModeSelector>();
            _specProvider = Substitute.For<ISpecProvider>();
            _blockTree = Substitute.For<IBlockTree>();
            _receiptStorage = new InMemoryReceiptStorage();
            _syncPeerPool = Substitute.For<ISyncPeerPool>();
            _syncConfig = new SyncConfig();
            _syncReport = Substitute.For<ISyncReport>();
            
            _feed = new FastReceiptsSyncFeed(
                _selector,
                _specProvider,
                _blockTree,
                _receiptStorage,
                _syncPeerPool,
                _syncConfig,
                _syncReport,
                LimboLogs.Instance);
        }
    }
}