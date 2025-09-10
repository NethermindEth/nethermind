// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class AccountStructDecoderTests
{
    [Test]
    public void Can_read_hashes_only()
    {
        AccountStruct account = new AccountStruct(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
        AccountStructDecoder decoder = new();
        Rlp rlp = decoder.Encode(account);
        ReadOnlySpan<byte> data = rlp.Bytes.AsSpan();
        Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(data);
        (ValueHash256 codeHash, ValueHash256 storageRoot) = decoder.DecodeHashesOnly(ref ctx);
        Assert.That(TestItem.KeccakA, Is.EqualTo(codeHash));
        Assert.That(TestItem.KeccakB, Is.EqualTo(storageRoot));
    }

    [Test]
    public void Roundtrip_test()
    {
        AccountStruct account = new AccountStruct(100).WithChangedCodeHash(TestItem.KeccakA).WithChangedStorageRoot(TestItem.KeccakB);
        AccountStructDecoder decoder = new();
        Rlp rlp = decoder.Encode(account);
        AccountStruct? decoded = decoder.Decode(new RlpStream(rlp.Bytes))!;
        Assert.That((int)decoded.Value.Balance, Is.EqualTo(100));
        Assert.That((int)decoded.Value.Nonce, Is.EqualTo(0));
        Assert.That(TestItem.KeccakA, Is.EqualTo(decoded.Value.CodeHash));
        Assert.That(TestItem.KeccakB, Is.EqualTo(decoded.Value.StorageRoot));
    }
}
