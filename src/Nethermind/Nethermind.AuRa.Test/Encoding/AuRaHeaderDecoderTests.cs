// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
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
    private static readonly AuRaHeaderDecoder _decoder = new();

    private static byte[] DeterministicSignature(int seed)
    {
        byte[] signature = new byte[64];
        new Random(seed).NextBytes(signature);
        return signature;
    }

    [Test]
    public void Can_decode_aura()
    {
        BlockHeader header = Build.A.BlockHeader.WithAura(100000000, DeterministicSignature(0xA5A5)).TestObject;

        Rlp rlp = _decoder.Encode(header);
        RlpReader decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = _decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash));
    }

    /// <summary>Round-trip stability: encoding a decoded header reproduces the exact bytes.</summary>
    [Test]
    public void AuRa_roundtrip_bytes_are_stable()
    {
        BlockHeader original = Build.A.BlockHeader.WithAura(42, DeterministicSignature(1)).TestObject;

        byte[] firstPass = _decoder.Encode(original).Bytes;
        RlpReader ctx = new(firstPass);
        BlockHeader? decoded = _decoder.Decode(ref ctx);
        byte[] secondPass = _decoder.Encode(decoded).Bytes;

        Assert.That(secondPass, Is.EqualTo(firstPass));
        Assert.That(decoded, Is.InstanceOf<AuRaBlockHeader>());
    }

    /// <summary>
    /// The seal section after the 13 base fields must be (step, signature) — never a 32-byte mixHash
    /// followed by an 8-byte nonce. Catches accidental Ethash-shape regressions.
    /// </summary>
    [Test]
    public void AuRa_seal_section_is_step_and_signature_not_mixHash_and_nonce()
    {
        byte[] signature = DeterministicSignature(2);
        BlockHeader header = Build.A.BlockHeader.WithAura(42, signature).TestObject;

        byte[] encoded = _decoder.Encode(header).Bytes;
        RlpReader ctx = new(encoded);
        ctx.ReadSequenceLength();
        for (int i = 0; i < 13; i++) ctx.SkipItem();

        (int _, int sealItemLen) = ctx.PeekPrefixAndContentLength();
        Assert.That(sealItemLen, Is.Not.EqualTo(Nethermind.Core.Crypto.Hash256.Size));

        long decodedStep = (long)ctx.DecodeUInt256();
        byte[]? decodedSignature = ctx.DecodeByteArray();
        Assert.That(decodedStep, Is.EqualTo(42L));
        Assert.That(decodedSignature, Is.EqualTo(signature));
    }

    /// <summary>Post-merge headers keep the Ethash/PoS seal shape; the AuRa decoder must fall back to the base shape.</summary>
    [Test]
    public void Decodes_PoS_shaped_header_as_base_header()
    {
        BlockHeader header = Build.A.BlockHeader.WithMixHash(TestItem.KeccakA).TestObject;

        byte[] encoded = _decoder.Encode(header).Bytes;
        RlpReader ctx = new(encoded);
        BlockHeader? decoded = _decoder.Decode(ref ctx);

        Assert.That(decoded, Is.Not.InstanceOf<AuRaBlockHeader>());
        Assert.That(decoded!.MixHash, Is.EqualTo(header.MixHash));
        Assert.That(decoded.Nonce, Is.EqualTo(header.Nonce));
    }

    /// <summary>
    /// Between <c>AuRaBlockProducer.PrepareBlock</c> (stamps step) and <c>AuRaSealer.SealBlock</c>
    /// (stamps signature) the header passes through <c>BlockProcessor.PrepareBlockForProcessing</c>,
    /// which clones it — the clone must keep the AuRa subclass or the sealer throws.
    /// </summary>
    [Test]
    public void CloneForProcessing_preserves_step_only_AuRa_header()
    {
        AuRaBlockHeader src = (AuRaBlockHeader)Build.A.BlockHeader.WithAura(123, null).TestObject;

        BlockHeader clone = src.CloneForProcessing();

        Assert.That(clone, Is.InstanceOf<AuRaBlockHeader>());
        AuRaBlockHeader auraClone = (AuRaBlockHeader)clone;
        Assert.That(auraClone.AuRaStep, Is.EqualTo(123L));
        Assert.That(auraClone.AuRaSignature, Is.Null);
    }

    [Test]
    public void IAuRaSealedHeader_distinguishes_subclass_from_base_BlockHeader()
    {
        Assert.That(Build.A.BlockHeader.TestObject, Is.Not.InstanceOf<IAuRaSealedHeader>());
        Assert.That(Build.A.BlockHeader.WithAura(0, []).TestObject, Is.InstanceOf<IAuRaSealedHeader>());
    }
}
