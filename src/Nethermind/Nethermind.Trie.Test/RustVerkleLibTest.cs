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
        public void ValidProof()
        {
            UIntPtr size = (UIntPtr) 10;
            var one = new byte[] {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            var one_32 = new byte[] {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.verkle_trie_new();

            RustVerkleLib.verkle_trie_insert(trie, one, one);
            RustVerkleLib.verkle_trie_insert(trie, one_32, one);
        }
        
        [Test]
        public void InValidProof()
        {
            UIntPtr size = (UIntPtr) 10;
            var one = new byte[] {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
            var one_32 = new byte[] {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1};

            IntPtr trie = RustVerkleLib.verkle_trie_new();
            
            RustVerkleLib.verkle_trie_insert(trie, one, one);
            RustVerkleLib.verkle_trie_insert(trie, one_32, one);
        }
    }
}
