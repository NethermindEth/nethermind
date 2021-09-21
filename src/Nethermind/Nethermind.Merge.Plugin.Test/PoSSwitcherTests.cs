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
        public void Is_pos()
        {
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance);
            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(100L).WithParentHash(Keccak.OfAnEmptyString).TestObject;
            BlockHeader blockHeader2 = Build.A.BlockHeader.WithTotalDifficulty(300L).WithParentHash(Keccak.Compute("terminal")).TestObject;
            BlockHeader blockHeader3 = Build.A.BlockHeader.WithTotalDifficulty(400L).TestObject;

            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader, false));
            
            poSSwitcher.SetTerminalTotalDifficulty(300);
            
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader, false));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2, false));
            
            poSSwitcher.SetTerminalPoWHash(Keccak.Compute("terminal"));
            
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader, false));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2, false));

            poSSwitcher.IsPos(blockHeader2, true);
            
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader3, false));
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader, false));
            
            poSSwitcher.IsPos(blockHeader, true);
            poSSwitcher.SetTerminalTotalDifficulty(0L);
            poSSwitcher.SetTerminalPoWHash(Keccak.OfAnEmptyString);
            
            Assert.AreEqual(false, poSSwitcher.IsPos(blockHeader, false));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader2, false));
            Assert.AreEqual(true, poSSwitcher.IsPos(blockHeader3, false));
        }
        
        [Test]
        public void Was_ever_in_pos()
        {
            PoSSwitcher poSSwitcher = new(NUnitLogManager.Instance);
            
            Assert.AreEqual(false,poSSwitcher.WasEverInPoS());
            
            poSSwitcher.SetTerminalTotalDifficulty(25L);
            BlockHeader blockHeader = Build.A.BlockHeader.WithTotalDifficulty(30L).TestObject;
            poSSwitcher.IsPos(blockHeader, true);
            
            Assert.AreEqual(true,poSSwitcher.WasEverInPoS());
            
            poSSwitcher.SetTerminalTotalDifficulty(1L);
            
            Assert.AreEqual(true,poSSwitcher.WasEverInPoS());
        }
    }
}

