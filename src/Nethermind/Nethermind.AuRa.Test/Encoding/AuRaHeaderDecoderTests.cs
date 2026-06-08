// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Encoding;

[TestFixture]
public class AuRaHeaderDecoderTests
{
    [Test]
    public void Can_decode_aura()
    {
        byte[] auRaSignature = new byte[64];
        new Random().NextBytes(auRaSignature);
        BlockHeader header = Build.A.BlockHeader
            .WithAura(100000000, auRaSignature)
            .TestObject;

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);
        Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }
}
