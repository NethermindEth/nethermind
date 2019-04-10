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

using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class BlockDownloaderTests
    {
        [Test]
        public void Test()
        {
            IDb blockDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            IDb headerDb = new MemDb();
            BlockTree blockTree = new BlockTree(blockDb, headerDb, blockInfoDb, GoerliSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            BlockDownloader blockDownloader = new BlockDownloader(blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);   
//            blockDownloader.DownloadBlocks()
        }
    }
}