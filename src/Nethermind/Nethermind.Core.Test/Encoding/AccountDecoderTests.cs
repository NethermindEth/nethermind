// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            AccountDecoder decoder = new();
            Rlp rlp = decoder.Encode(account);
            (Keccak codeHash, Keccak storageRoot) = decoder.DecodeHashesOnly(new RlpStream(rlp.Bytes));
            Assert.AreEqual(codeHash, TestItem.KeccakA);
            Assert.AreEqual(storageRoot, TestItem.KeccakB);
        }

        [Test]
        public void Roundtrip_test()
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new();
            Rlp rlp = decoder.Encode(account);
            Account decoded = decoder.Decode(new RlpStream(rlp.Bytes))!;
            Assert.AreEqual((int)decoded.Balance, 100);
            Assert.AreEqual((int)decoded.Nonce, 0);
            Assert.AreEqual(decoded.CodeHash, TestItem.KeccakA);
            Assert.AreEqual(decoded.StorageRoot, TestItem.KeccakB);
        }
    }
}
