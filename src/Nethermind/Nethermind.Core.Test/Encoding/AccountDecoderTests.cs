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
            Rlp.ValueDecoderContext ctx = new(rlp.Bytes);
            (Hash256 codeHash, Hash256 storageRoot) = decoder.DecodeHashesOnly(ref ctx);
            Assert.That(TestItem.KeccakA, Is.EqualTo(codeHash));
            Assert.That(TestItem.KeccakB, Is.EqualTo(storageRoot));
        }

        [Test]
        public void Roundtrip_test()
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new();
            Rlp rlp = decoder.Encode(account);
            Rlp.ValueDecoderContext ctx = new(rlp.Bytes);
            Account decoded = decoder.Decode(ref ctx)!;
            Assert.That((int)decoded.Balance, Is.EqualTo(100));
            Assert.That((int)decoded.Nonce, Is.EqualTo(0));
            Assert.That(TestItem.KeccakA, Is.EqualTo(decoded.CodeHash));
            Assert.That(TestItem.KeccakB, Is.EqualTo(decoded.StorageRoot));
        }

        [Test]
        public void DecodeStorageRootOnly_returns_correct_value_and_advances_past_it()
        {
            Account account = new Account(100).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new();
            Rlp rlp = decoder.Encode(account);
            Rlp.ValueDecoderContext ctx = new(rlp.Bytes);

            int positionBefore = ctx.Position;
            ctx.SkipLength();
            ctx.SkipItem(); // nonce
            ctx.SkipItem(); // balance
            int positionBeforeStorageRoot = ctx.Position;

            ctx.Position = positionBefore;
            Hash256 storageRoot = decoder.DecodeStorageRootOnly(ref ctx);

            Assert.That(storageRoot, Is.EqualTo(TestItem.KeccakB));
            Assert.That(ctx.Position, Is.GreaterThan(positionBeforeStorageRoot));
        }
    }
}
