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

using System.IO;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class PoSSwitcherTests
    {
        [Test]
        public void Is_pos_with_total_difficulty()
        {
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(1);
            BlockTree blockTree = blockTreeBuilder.TestObject;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(100L).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(200L).TestObject;
            BlockHeader blockHeader3 = Build.A.BlockHeader.WithTotalDifficulty(300L).TestObject;

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader3));

            poSSwitcher.TerminalTotalDifficulty = 500;

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader2));
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader3));
        }

        [Test]
        public void Is_pos_with_terminal_hash()
        {
            PoSSwitcher poSSwitcher = CreatePosSwitcher(1000000000000000);
            poSSwitcher.SetTerminalPoWHash(Keccak.Compute("test1"));

            BlockHeader blockHeader = Build.A.BlockHeader.WithParentHash(Keccak.Compute("test2")).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithParentHash(Keccak.Compute("test1")).TestObject;

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));

            poSSwitcher.SetTerminalPoWHash(Keccak.Compute("test2"));

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader2));
        }

        [Test]
        public void Is_pos_try_switch_to_PoS()
        {
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(200L).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(400L).TestObject;

            Assert.AreEqual(true, poSSwitcher.TrySwitchToPos(blockHeader));
            poSSwitcher.TerminalTotalDifficulty = 500;

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
        }

        [Test]
        public void Is_pos_sets_first_PoS_header_once()
        {
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(200L).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(400L).TestObject;

            poSSwitcher.TrySwitchToPos(blockHeader);
            poSSwitcher.TerminalTotalDifficulty = 400;

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));

            poSSwitcher.TrySwitchToPos(blockHeader2);

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
        }

        [Test]
        public void Has_ever_been_in_pos()
        {
            PoSSwitcher poSSwitcher = CreatePosSwitcher(200);

            Assert.AreEqual(false, poSSwitcher.HasEverBeenInPos());

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(300L).TestObject;
            poSSwitcher.TrySwitchToPos(blockHeader);

            Assert.AreEqual(true, poSSwitcher.HasEverBeenInPos());
        }
        
        [Test]
        [Ignore("I'm waiting for the spec to be confirmed. For now, we're setting TTD only in config. " +
                "If a consensus client can override TTD, we need to add persistence of this parameter.")]
        public void Can_load_parameters_after_the_restart()
        {
            using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);

            SimpleFilePublicKeyDb filePublicKeyDb = new("PoSSwitcherTests", Path.GetTempPath(), LimboLogs.Instance);
            UInt256 configTerminalTotalDifficulty = 10L;
            UInt256 expectedTotalTerminalDifficulty = 200L;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(configTerminalTotalDifficulty, filePublicKeyDb);
            poSSwitcher.TerminalTotalDifficulty = expectedTotalTerminalDifficulty;

            PoSSwitcher newPoSSwitcher = CreatePosSwitcher(configTerminalTotalDifficulty, filePublicKeyDb);
            
            tempPath.Dispose();
            Assert.AreEqual(expectedTotalTerminalDifficulty, newPoSSwitcher.TerminalTotalDifficulty);
        }

        private static PoSSwitcher CreatePosSwitcher(UInt256 terminalTotalDifficulty, IDb? db = null)
        {
            db ??= new MemDb();
            MergeConfig? mergeConfig = new() {Enabled = true, TerminalTotalDifficulty = terminalTotalDifficulty};
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(1);
            BlockTree blockTree = blockTreeBuilder.TestObject;
            return new PoSSwitcher(LimboLogs.Instance, mergeConfig, db, blockTree);
        }
    }
}
