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

        AuRaHeaderDecoder decoder = new();
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

        AuRaHeaderDecoder decoder = new();
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

        AuRaHeaderDecoder decoder = new();
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

    /// <summary>
    /// Regression guard for PR-#11938 review fix: between <c>AuRaBlockProducer.PrepareBlock</c>
    /// (stamps step) and <c>AuRaSealer.SealBlock</c> (stamps signature) the header passes
    /// through <c>BlockProcessor.PrepareBlockForProcessing</c>. That rebuild used to gate on
    /// TryGetSeal (which requires both step + signature), so a step-only header was demoted
    /// to plain <c>BlockHeader</c> and AuRaSealer then threw.
    /// <see cref="AuRaSealedHeaderExtensions.CopyAuRaSeal"/> must preserve the partial seal.
    /// </summary>
    [Test]
    public void CopyAuRaSeal_preserves_step_only_AuRa_header()
    {
        AuRaBlockHeader src = (AuRaBlockHeader)Build.A.BlockHeader.WithAura(123, null).TestObject;
        AuRaBlockHeader dst = (AuRaBlockHeader)Build.A.BlockHeader.WithAura(0, []).TestObject;
        dst.AuRaStep = null;
        dst.AuRaSignature = null;

        AuRaSealedHeaderExtensions.CopyAuRaSeal(src, dst);

        Assert.That(dst.AuRaStep, Is.EqualTo(123L), "step copied");
        Assert.That(dst.AuRaSignature, Is.Null, "null signature preserved");
    }

    [Test]
    public void CopyAuRaSeal_noop_when_either_header_is_not_AuRa()
    {
        BlockHeader plain = Build.A.BlockHeader.TestObject;
        AuRaBlockHeader aura = (AuRaBlockHeader)Build.A.BlockHeader.WithAura(7, new byte[64]).TestObject;

        // plain → aura: nothing to copy from, dst step/sig must stay as initialized.
        AuRaBlockHeader dstFromPlain = (AuRaBlockHeader)Build.A.BlockHeader.WithAura(0, []).TestObject;
        dstFromPlain.AuRaStep = null;
        dstFromPlain.AuRaSignature = null;
        AuRaSealedHeaderExtensions.CopyAuRaSeal(plain, dstFromPlain);
        Assert.That(dstFromPlain.AuRaStep, Is.Null, "no copy from non-AuRa source");

        // aura → plain: dst can't hold seal, must remain plain BlockHeader (no throw).
        BlockHeader dstPlain = Build.A.BlockHeader.TestObject;
        Assert.DoesNotThrow(() => AuRaSealedHeaderExtensions.CopyAuRaSeal(aura, dstPlain));
    }

    [Test]
    public void IsAuRa_distinguishes_subclass_from_base_BlockHeader()
    {
        Assert.That(Build.A.BlockHeader.TestObject.IsAuRa(), Is.False);
        Assert.That(Build.A.BlockHeader.WithAura(0, []).TestObject.IsAuRa(), Is.True);
    }
}
