// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

public class SignatureTests
{
    [TestCase(27ul, null)]
    [TestCase(28ul, null)]
    [TestCase(35ul, 0)]
    [TestCase(36ul, 0)]
    [TestCase(37ul, 1)]
    [TestCase(38ul, 1)]
    [TestCase(35ul + 2 * 314158, 314158)]
    [TestCase(36ul + 2 * 314158, 314158)]
    public void Test(ulong v, int? chainId)
    {
        Signature signature = new(0, 0, v);
        Assert.That(signature.ChainId, Is.EqualTo(chainId));
    }

    [TestCase(27ul, TestName = "EthSignV27")]
    [TestCase(28ul, TestName = "EthSignV28")]
    [TestCase(0ul, TestName = "ZeroV")]
    [TestCase(0x100ul, TestName = "OddDigitV_NeedsLeadingZero")]
    [TestCase(0xFFul, TestName = "TwoDigitV")]
    [TestCase(ulong.MaxValue, TestName = "MaxV_SixteenDigits")]
    [TestCase(35ul + 2 * 314158, TestName = "ChainIdEncodedV")]
    public void SignatureConverter_WriteMatchesToString(ulong v)
    {
        Span<byte> rs = stackalloc byte[64];
        for (int i = 0; i < rs.Length; i++) rs[i] = (byte)(i * 7 + 3);
        Signature signature = new(rs, recoveryId: 0)
        {
            V = v
        };

        JsonSerializerOptions options = new();
        options.Converters.Add(new SignatureConverter());

        string fromConverter = JsonSerializer.Serialize(signature, options);
        string fromToString = $"\"{signature}\"";

        Assert.That(fromConverter, Is.EqualTo(fromToString), "the converter must produce the same wire format as Signature.ToString()");
    }

    [Test]
    public void can_recover_from_message()
    {
        string messageHex =
            "8F0120AB288C789ACF066672F9CBDDB551B921C8D1B2039361BA95970894BFBF5262062492F5A33D3EACE2929082574F1F0CED1A65FEA66D78FB7439DF2BA54B48F38B495EADCBA5F584D59F455467D376122C6A9B759AA3A973C1707BC67DA10001E004CB840AD062CF827668827668CB840AD062CF82766682766686016755793C86";
        byte[] messageBytes = Bytes.FromHexString(messageHex);
        byte[] mdc = messageBytes[..32];
        byte[] signature = messageBytes.Slice(32, 65);
        byte[] messageType = new[] { messageBytes[97] };
        byte[] data = messageBytes[98..];
        byte[] signatureSlice = signature[..64];
        byte recoveryId = signature[64];
        Signature signatureObject = new(signatureSlice, recoveryId);
        Hash256 keccak = Keccak.Compute(Bytes.Concat(messageType, data));
        Span<byte> publicKey = stackalloc byte[65];
        bool result = SecP256k1.RecoverKeyFromCompact(publicKey, keccak.Bytes, signatureObject.Bytes, signatureObject.RecoveryId, false);
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasLowS_AcceptsCanonicalSignature()
    {
        PrivateKey key = new PrivateKeyGenerator().Generate();
        Signature signature = new EthereumEcdsa(0).Sign(key, Hash256.Zero);
        Assert.That(signature.HasLowS(), Is.True);
    }

    [Test]
    public void HasLowS_RejectsMalleableCounterpart()
    {
        PrivateKey key = new PrivateKeyGenerator().Generate();
        Signature signature = new EthereumEcdsa(0).Sign(key, Hash256.Zero);
        Signature flipped = FlipSignature(signature);
        Assert.That(signature, Is.Not.EqualTo(flipped));
        Assert.That(signature.HasLowS(), Is.True);
        Assert.That(flipped.HasLowS(), Is.False);
    }

    [Test]
    public void HasLowS_BoundaryAtHalfN()
    {
        Signature atHalf = new(in UInt256.Zero, SecP256k1Curve.HalfN, Signature.VOffset);
        Assert.That(atHalf.HasLowS(), Is.True);

        Signature overHalf = new(in UInt256.Zero, SecP256k1Curve.HalfNPlusOne, Signature.VOffset);
        Assert.That(overHalf.HasLowS(), Is.False);

        Signature minS = new(in UInt256.Zero, UInt256.One, Signature.VOffset);
        Assert.That(minS.HasLowS(), Is.True);
    }

    private static Signature FlipSignature(Signature original)
    {
        ReadOnlySpan<byte> bytes = original.Bytes;
        UInt256 s = new(bytes[32..], true);
        UInt256 sNew = SecP256k1Curve.N - s;

        byte[] result = new byte[65];
        bytes[..32].CopyTo(result);
        sNew.ToBigEndian(result.AsSpan(32, 32));
        result[64] = original.V == Signature.VOffset ? (byte)28 : (byte)27;
        return new Signature(result);
    }
}
