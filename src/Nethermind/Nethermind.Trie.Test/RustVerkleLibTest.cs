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
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class RustVerkleLibTest
    {
        [Test]
        public void TestInsertGet()
        {
            byte[] one = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            byte[] one32 = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.VerkleTrieNew();

            RustVerkleLib.VerkleTrieInsert(trie, one, one32);
            RustVerkleLib.VerkleTrieInsert(trie, one32, one);
            
            byte[] array32 = RustVerkleLib.VerkleTrieGet(trie, one32);
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(one[i],array32[i]);
            }
            
            byte[] array = RustVerkleLib.VerkleTrieGet(trie, one);
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(one32[i],array[i]);
            }
        }
    }
}
