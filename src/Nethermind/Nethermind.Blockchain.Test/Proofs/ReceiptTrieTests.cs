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
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs
{
    public class ReceiptTrieTests
    {
        [Test]
        public void Can_calculate_root_no_eip_658()
        {
            TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
            ReceiptTrie receiptTrie = new ReceiptTrie(1, MainNetSpecProvider.Instance, new [] {receipt});
            Assert.AreEqual("0xe51a2d9f986d68628990c9d65e45c36128ec7bb697bd426b0bb4d18a3f3321be", receiptTrie.RootHash.ToString());
        }
        
        [Test]
        public void Can_calculate_root()
        {
            TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
            ReceiptTrie receiptTrie = new ReceiptTrie(MainNetSpecProvider.MuirGlacierBlockNumber, MainNetSpecProvider.Instance, new [] {receipt});
            Assert.AreEqual("0x2e6d89c5b539e72409f2e587730643986c2ef33db5e817a4223aa1bb996476d5", receiptTrie.RootHash.ToString());
        }
    }
}