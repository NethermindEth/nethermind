//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.IO;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class PoSSwitcherTests
    {
        [Test]
        [Ignore("Need to ensure that transition process is correct")]
        public void Correctly_validate_headers_with_TTD()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200, blockTree);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(100L).WithNumber(1).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(200L).WithNumber(2).TestObject;
            BlockHeader blockHeader3 = Build.A.BlockHeader.WithTotalDifficulty(300L).WithNumber(3).TestObject;

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader3));
        }

        [Test]
        [Ignore("Need to ensure that transition process is correct")]
        public void Switch_with_terminal_hash()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = CreatePosSwitcher(1000000000000000, blockTree);
            poSSwitcher.SetTerminalPoWHash(Keccak.Compute("test1"));

            Block firstBlock = Build.A.Block.WithParentHash(Keccak.Compute("test2")).WithTotalDifficulty(100L).WithNumber(1).TestObject;
            Block secondBlock = Build.A.Block.WithParentHash(Keccak.Compute("test1")).WithTotalDifficulty(200L).WithNumber(2).TestObject;
            blockTree.NewHeadBlock += Raise.Event<EventHandler<BlockEventArgs>>(new BlockEventArgs(firstBlock));
            blockTree.NewHeadBlock += Raise.Event<EventHandler<BlockEventArgs>>(new BlockEventArgs(secondBlock));

            Assert.AreEqual(false, poSSwitcher.IsPos(firstBlock.Header));
            Assert.AreEqual(true, poSSwitcher.IsPos(secondBlock.Header));
        }

        [Test]
        [Ignore("Need to ensure that transition process is correct")]
        public void Is_pos_without_switch_return_expected_results()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200, blockTree);
            Block firstBlock = Build.A.Block.WithTotalDifficulty(100L).WithNumber(1).TestObject;
            Block secondBlock = Build.A.Block.WithTotalDifficulty(200L).WithNumber(2).TestObject;
            Block thirdBlock = Build.A.Block.WithTotalDifficulty(400L).WithNumber(3).TestObject;

            Assert.AreEqual(false, poSSwitcher.IsPos(firstBlock.Header));
            Assert.AreEqual(true, poSSwitcher.IsPos(secondBlock.Header));
            Assert.AreEqual(true, poSSwitcher.IsPos(thirdBlock.Header));
        }

        [Test]
        [Ignore("Need to ensure that transition process is correct")]
        public void Is_pos__with_switch_return_expected_results()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200, blockTree);
            Block firstBlock = Build.A.Block.WithTotalDifficulty(100L).WithNumber(1).TestObject;
            blockTree.NewHeadBlock += Raise.Event<EventHandler<BlockEventArgs>>(new BlockEventArgs(firstBlock));
            Block secondBlock = Build.A.Block.WithTotalDifficulty(200L).WithNumber(2).TestObject;
            blockTree.NewHeadBlock += Raise.Event<EventHandler<BlockEventArgs>>(new BlockEventArgs(secondBlock));
            Block thirdBlock = Build.A.Block.WithTotalDifficulty(400L).WithNumber(3).TestObject;
            blockTree.NewHeadBlock += Raise.Event<EventHandler<BlockEventArgs>>(new BlockEventArgs(thirdBlock));

            Assert.AreEqual(false, poSSwitcher.IsPos(firstBlock.Header));
            Assert.AreEqual(true, poSSwitcher.IsPos(secondBlock.Header));
            Assert.AreEqual(true, poSSwitcher.IsPos(thirdBlock.Header));
        }

        [Test]
        public void Switch_when_TTD_is_reached()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200, blockTree);

            Assert.AreEqual(false, poSSwitcher.HasEverReachedTerminalPoWBlock());
            Block block = Build.A.Block.WithTotalDifficulty(300L).WithNumber(1).TestObject;
            blockTree.NewHeadBlock += Raise.Event<EventHandler<BlockEventArgs>>(new BlockEventArgs(block));

            Assert.AreEqual(true, poSSwitcher.HasEverReachedTerminalPoWBlock());
        }
        
        [Test]
        public void Can_load_parameters_after_the_restart()
        {
            using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);

            SimpleFilePublicKeyDb filePublicKeyDb = new("PoSSwitcherTests", Path.GetTempPath(), LimboLogs.Instance);
            UInt256 configTerminalTotalDifficulty = 10L;
            UInt256 expectedTotalTerminalDifficulty = 200L;
            var blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = CreatePosSwitcher(configTerminalTotalDifficulty, blockTree, filePublicKeyDb);
            poSSwitcher.TerminalTotalDifficulty = expectedTotalTerminalDifficulty;

            PoSSwitcher newPoSSwitcher = CreatePosSwitcher(configTerminalTotalDifficulty,blockTree, filePublicKeyDb);
            
            tempPath.Dispose();
            Assert.AreEqual(expectedTotalTerminalDifficulty, newPoSSwitcher.TerminalTotalDifficulty);
        }

        private static PoSSwitcher CreatePosSwitcher(UInt256 terminalTotalDifficulty, IBlockTree blockTree, IDb? db = null)
        {
            db ??= new MemDb();
            MergeConfig? mergeConfig = new() {Enabled = true, TerminalTotalDifficulty = terminalTotalDifficulty};
            return new PoSSwitcher(LimboLogs.Instance, mergeConfig, db, blockTree, MainnetSpecProvider.Instance);
        }
    }
}
