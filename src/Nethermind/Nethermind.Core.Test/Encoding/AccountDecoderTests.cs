// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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
            RlpReader ctx = Encode(decoder, account);
            (Hash256 codeHash, Hash256 storageRoot) = decoder.DecodeHashesOnly(ref ctx);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(TestItem.KeccakA, Is.EqualTo(codeHash));
                Assert.That(TestItem.KeccakB, Is.EqualTo(storageRoot));
            }
        }

        private static readonly UInt256[] Words =
        [
            UInt256.Zero, UInt256.One, 127, 128, 255, 256, ulong.MaxValue, (UInt256)ulong.MaxValue + 1,
            UInt256.One << 127, (UInt256.One << 128) - 1, UInt256.One << 200, UInt256.MaxValue
        ];

        [Test]
        public void EncodeAsBytes_matches_the_writer_encoding(
            [Values(0UL, 1UL, 127UL, 128UL, 255UL, 256UL, ulong.MaxValue)] ulong nonce, [ValueSource(nameof(Words))] UInt256 balance, [Values] bool slim, [Values] bool hasStorageAndCode)
        {
            Account account = new(nonce, balance);
            if (hasStorageAndCode) account = account.WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new(slim);
            byte[] expected = new byte[decoder.GetLength(account)];
            RlpWriter writer = new(expected);
            decoder.Encode(account, ref writer);

            Assert.That(decoder.EncodeAsBytes(account), Is.EqualTo(expected));
        }

        [Test]
        public void Roundtrip_test()
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new();
            RlpReader ctx = Encode(decoder, account);
            Account decoded = decoder.Decode(ref ctx)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Balance, Is.EqualTo((UInt256)100));
                Assert.That(decoded.Nonce, Is.EqualTo(0UL));
                Assert.That(TestItem.KeccakA, Is.EqualTo(decoded.CodeHash));
                Assert.That(TestItem.KeccakB, Is.EqualTo(decoded.StorageRoot));
            }
        }

        [Test]
        public void DecodeStorageRootOnly_returns_correct_value_and_advances_past_it()
        {
            Account account = new Account(100).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new();
            RlpReader ctx = Encode(decoder, account);

            int positionBefore = ctx.Position;
            ctx.SkipLength();
            ctx.SkipItem(); // nonce
            ctx.SkipItem(); // balance
            int positionBeforeStorageRoot = ctx.Position;

            ctx.Position = positionBefore;
            Hash256 storageRoot = decoder.DecodeStorageRootOnly(ref ctx);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(storageRoot, Is.EqualTo(TestItem.KeccakB));
                Assert.That(ctx.Position, Is.GreaterThan(positionBeforeStorageRoot));
            }
        }

        [Test]
        public void List_length_must_match_the_fields([Values(-1, 0, 1)] int lengthDelta, [Values] bool asStruct)
        {
            Account account = new Account(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
            AccountDecoder decoder = new();
            // The trailing empty string becomes an extra item once the list claims one more byte.
            byte[] bytes = [.. decoder.EncodeAsBytes(account), Rlp.EmptyByteArrayByte];
            (int prefixLength, _) = new RlpReader(bytes).PeekPrefixAndContentLength();
            bytes[prefixLength - 1] = (byte)(bytes[prefixLength - 1] + lengthDelta);

            Action decode = asStruct
                ? () => decoder.TryDecodeStruct(bytes, out _)
                : () =>
                {
                    RlpReader ctx = new(bytes);
                    decoder.Decode(ref ctx);
                };

            if (lengthDelta == 0)
            {
                Assert.That(decode, Throws.Nothing);
            }
            else
            {
                Assert.That(decode, Throws.TypeOf<RlpException>());
            }
        }

        private static RlpReader Encode(AccountDecoder decoder, Account account)
        {
            byte[] bytes = new byte[decoder.GetLength(account)];
            RlpWriter writer = new(bytes);
            decoder.Encode(ref writer, account);
            return new RlpReader(bytes);
        }
    }
}
