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

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class AccountDecoderTests
    {
        [Test]
        public void Can_read_hashes_only()
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new AccountDecoder();
            Rlp rlp = decoder.Encode(account);
            (Keccak codeHash, Keccak storageRoot) = decoder.DecodeHashesOnly(new RlpStream(rlp.Bytes));
            Assert.AreEqual(codeHash, TestItem.KeccakA);
            Assert.AreEqual(storageRoot, TestItem.KeccakB);
        }
        
        [Test]
        public void Roundtrip_test()
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new AccountDecoder();
            Rlp rlp = decoder.Encode(account);
            Account decoded = decoder.Decode(new RlpStream(rlp.Bytes));
            Assert.AreEqual((int)decoded.Balance, 100);
            Assert.AreEqual((int)decoded.Nonce, 0);
            Assert.AreEqual(decoded.CodeHash, TestItem.KeccakA);
            Assert.AreEqual(decoded.StorageRoot, TestItem.KeccakB);
        }
    }
}
