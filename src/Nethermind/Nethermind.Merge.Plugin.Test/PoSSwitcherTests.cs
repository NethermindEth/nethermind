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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class PoSSwitcherTests
    {
        [Test]
        public void Is_pos_with_total_difficulty()
        {
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance, new MergeConfig());
            poSSwitcher.SetTerminalTotalDifficulty(200);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(100L).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(200L).TestObject;
            BlockHeader blockHeader3 = Build.A.BlockHeader.WithTotalDifficulty(300L).TestObject;

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader3));

            poSSwitcher.SetTerminalTotalDifficulty(500);

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader2));
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader3));
        }

        [Test]
        public void Is_pos_with_terminal_hash()
        {
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance, new MergeConfig());
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
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance, new MergeConfig());
            poSSwitcher.SetTerminalTotalDifficulty(200);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(200L).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(400L).TestObject;

            Assert.AreEqual(true, poSSwitcher.TrySwitchToPos(blockHeader));
            poSSwitcher.SetTerminalTotalDifficulty(500);

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
        }

        [Test]
        public void Is_pos_sets_first_PoS_header_once()
        {
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance, new MergeConfig());
            poSSwitcher.SetTerminalTotalDifficulty(200);

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(200L).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(400L).TestObject;

            poSSwitcher.TrySwitchToPos(blockHeader);
            poSSwitcher.SetTerminalTotalDifficulty(400);

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));

            poSSwitcher.TrySwitchToPos(blockHeader2);

            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2));
        }

        [Test]
        public void Was_ever_in_pos()
        {
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance, new MergeConfig());
            poSSwitcher.SetTerminalTotalDifficulty(200L);

            Assert.AreEqual(false, poSSwitcher.HasEverBeenInPos());

            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(300L).TestObject;
            poSSwitcher.TrySwitchToPos(blockHeader);

            Assert.AreEqual(true, poSSwitcher.HasEverBeenInPos());
        }
    }
}
