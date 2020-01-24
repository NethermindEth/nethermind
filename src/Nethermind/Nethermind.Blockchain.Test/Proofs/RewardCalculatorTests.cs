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

using Nethermind.Blockchain.Proofs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs
{
    public class TxTrieTests
    {
        [Test]
        public void Can_calculate_root()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            TxTrie txTrie = new TxTrie(block);
            Assert.AreEqual("0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5", txTrie.RootHash.ToString());
        }
    }
}