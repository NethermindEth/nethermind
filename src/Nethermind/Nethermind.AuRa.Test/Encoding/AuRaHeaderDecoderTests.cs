// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa;
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
        // Deterministic seed so a flake here is reproducible.
        byte[] auRaSignature = new byte[64];
        new Random(0xA5A5).NextBytes(auRaSignature);
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

    /// <summary>
    /// Round-trip stability: encoding a decoded header reproduces the exact bytes.
    /// Catches asymmetric encode/decode bugs that <see cref="Can_decode_aura"/> would miss
    /// because both directions could share a wrong-but-consistent shape.
    /// </summary>
    [Test]
    public void AuRa_roundtrip_bytes_are_stable()
    {
        // Deterministic seal: 64-byte signature matching the gnosis genesis shape.
        byte[] auRaSignature = new byte[64];
        for (int i = 0; i < auRaSignature.Length; i++) auRaSignature[i] = (byte)i;

        BlockHeader original = Build.A.BlockHeader.WithAura(42, auRaSignature).TestObject;

        HeaderDecoder decoder = new();
        byte[] firstPass = decoder.Encode(original).Bytes;

        Rlp.ValueDecoderContext ctx = new(firstPass);
        BlockHeader? decoded = decoder.Decode(ref ctx);
        byte[] secondPass = decoder.Encode(decoded).Bytes;

        Assert.That(secondPass, Is.EqualTo(firstPass), "encode → decode → encode must be byte-identical");
        Assert.That(decoded, Is.InstanceOf<AuRaBlockHeader>(), "decoded header must carry the AuRa subclass");
    }

    /// <summary>
    /// Wire-format invariant: the seal section between extraData and (optional) baseFeePerGas
    /// must be (step, signature) for an AuRa header, never (mixHash, nonce). Skipping past the
    /// 13 base fields lands us at the seal section; we verify the next item is the step integer
    /// (1 byte for value 42) followed by the 64-byte signature — not a 32-byte hash.
    /// </summary>
    [Test]
    public void AuRa_seal_section_is_step_and_signature_not_mixHash_and_nonce()
    {
        byte[] auRaSignature = new byte[64];
        BlockHeader header = Build.A.BlockHeader.WithAura(42, auRaSignature).TestObject;

        HeaderDecoder decoder = new();
        byte[] encoded = decoder.Encode(header).Bytes;
        Rlp.ValueDecoderContext ctx = new(encoded);
        ctx.ReadSequenceLength();
        // Skip the 13 base fields (parentHash..extraData).
        for (int i = 0; i < 13; i++) ctx.SkipItem();

        (int _, int sealItemLen) = ctx.PeekPrefixAndContentLength();
        Assert.That(sealItemLen, Is.Not.EqualTo(Nethermind.Core.Crypto.Hash256.Size),
            "AuRa seal must NOT serialize the next item as a 32-byte hash (mixHash); that would be the Ethash shape.");

        long decodedStep = (long)ctx.DecodeUInt256();
        byte[]? decodedSignature = ctx.DecodeByteArray();
        Assert.That(decodedStep, Is.EqualTo(42L), "step at seal section");
        Assert.That(decodedSignature, Is.EqualTo(auRaSignature), "signature at seal section");
    }
}
