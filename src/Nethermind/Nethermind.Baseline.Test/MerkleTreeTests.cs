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

using System;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture]
    public class MerkleTreeTests
    {
        [Test]
        public void On_adding_one_leaf_count_goes_up_to_1()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void Initially_count_is_0()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void Can_restore_count_from_the_database()
        {
            throw new NotImplementedException();
        }
        
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void When_inserting_more_leaves_count_keeps_growing(int numberOfLeaves)
        {
            throw new NotImplementedException();
        }
        
        [TestCase(0)]
        [TestCase(short.MaxValue + 1)]
        public void Can_get_proof_from_an_emptyTree_on_an_index(int leafIndex)
        {
            throw new NotImplementedException();
        }
        
        [TestCase(0)]
        [TestCase(short.MaxValue + 1)]
        public void Can_get_proof_on_a_populated_trie_on_an_index(int leafIndex)
        {
            throw new NotImplementedException();
        }
        
        [TestCase(int.MinValue)]
        [TestCase(-1)]
        [TestCase(short.MaxValue + 2)]
        [TestCase(int.MaxValue)]
        public void Throws_on_get_proof_on_the_leaf_index_out_of_bounds(int leafIndex)
        {
            throw new NotImplementedException();
        }
    }
}