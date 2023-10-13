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
            Account account = new Account(100).WithChangedCodeHash(TestItem._commitmentA).WithChangedStorageRoot(TestItem._commitmentB);
            AccountDecoder decoder = new();
            Rlp rlp = decoder.Encode(account);
            (Commitment codeHash, Commitment storageRoot) = decoder.DecodeHashesOnly(new RlpStream(rlp.Bytes));
            Assert.That(TestItem._commitmentA, Is.EqualTo(codeHash));
            Assert.That(TestItem._commitmentB, Is.EqualTo(storageRoot));
        }

        [Test]
        public void Roundtrip_test()
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem._commitmentA).WithChangedStorageRoot(TestItem._commitmentB);
            AccountDecoder decoder = new();
            Rlp rlp = decoder.Encode(account);
            Account decoded = decoder.Decode(new RlpStream(rlp.Bytes))!;
            Assert.That((int)decoded.Balance, Is.EqualTo(100));
            Assert.That((int)decoded.Nonce, Is.EqualTo(0));
            Assert.That(TestItem._commitmentA, Is.EqualTo(decoded.CodeHash));
            Assert.That(TestItem._commitmentB, Is.EqualTo(decoded.StorageRoot));
        }
    }
}
