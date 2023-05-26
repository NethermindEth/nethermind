// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
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

    [Test]
    public void can_recover_from_message()
    {
        var messageHex =
            "8F0120AB288C789ACF066672F9CBDDB551B921C8D1B2039361BA95970894BFBF5262062492F5A33D3EACE2929082574F1F0CED1A65FEA66D78FB7439DF2BA54B48F38B495EADCBA5F584D59F455467D376122C6A9B759AA3A973C1707BC67DA10001E004CB840AD062CF827668827668CB840AD062CF82766682766686016755793C86";
        var messageBytes = Bytes.FromHexString(messageHex);
        var mdc = messageBytes[..32];
        var signature = messageBytes.Slice(32, 65);
        var messageType = new[] { messageBytes[97] };
        var data = messageBytes[98..];
        var signatureSlice = signature[..64];
        var recoveryId = signature[64];
        var signatureObject = new Signature(signatureSlice, recoveryId);
        var keccak = Keccak.Compute(Bytes.Concat(messageType, data));
        Span<byte> publicKey = stackalloc byte[65];
        bool result = SpanSecP256k1.RecoverKeyFromCompact(publicKey, keccak.Bytes, signatureObject.Bytes, signatureObject.RecoveryId, false);
        result.Should().BeTrue();
    }
}
