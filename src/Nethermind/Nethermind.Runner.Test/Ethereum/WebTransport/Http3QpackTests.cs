// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Runner.Ethereum.WebTransport;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.WebTransport;

public class Http3QpackTests
{
    private const string Origin = "5F4B10687474703A2F2F3132372E302E302E31";
    private const string LiteralOffer = "0000CFD7500F3132372E302E302E313A333537343251072F64657670327027023A70726F746F636F6C0C7765" +
        "627472616E73706F727427177365632D7765627472616E73706F72742D68747470332D647261667430320131" +
        "270F77742D617661696C61626C652D70726F746F636F6C73092277656274782F3122";
    private const string HuffmanOffer = "0000CFD7508B089D5C0B8170DC65B75A1751866242F7AC55FF2F00B95D8749C87A3F89F058D360EA4567B13F" +
        "2F0E4148B782C69B07522B3D895A74A6B65692C1CA900B810F2F09F09587DC66A071D055ABB0E9390F423F88" +
        "FE7C1634F9603FCF";

    [TestCase(LiteralOffer + Origin, "0000")]
    [TestCase(HuffmanOffer + Origin, "0000")]
    [TestCase(LiteralOffer + Origin, "0001")]
    [TestCase(LiteralOffer + Origin, "007F00")]
    public void Reads_webtransport_connect_offer(string fieldSection, string prefix)
    {
        bool decoded = Http3Qpack.TryReadOffer(Convert.FromHexString(prefix + fieldSection[4..]), out string offer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(offer, Is.EqualTo("\"webtx/1\""));
        }
    }

    [TestCase("2703636F6E6E656374696F6E05636C6F7365")]
    [TestCase("227465076465666C617465")]
    public void Rejects_connection_specific_fields(string header) =>
        Assert.That(Http3Qpack.TryReadOffer(Convert.FromHexString(LiteralOffer + Origin + header), out _), Is.False);

    [Test]
    public void Rejects_dynamic_table_references()
    {
        byte[] fields = Convert.FromHexString(LiteralOffer + Origin);
        fields[0] = 1;

        Assert.That(Http3Qpack.TryReadOffer(fields, out _), Is.False);
    }

    [Test]
    public void Rejects_huffman_padding_that_does_not_match_the_eos_prefix()
    {
        byte[] fields = Convert.FromHexString(HuffmanOffer + Origin);
        fields[(HuffmanOffer.Length / 2) - 1] ^= 1;

        Assert.That(Http3Qpack.TryReadOffer(fields, out _), Is.False);
    }

    [TestCase(LiteralOffer)]
    [TestCase(LiteralOffer + Origin + Origin)]
    public void Requires_exactly_one_origin_header(string fieldSection) =>
        Assert.That(Http3Qpack.TryReadOffer(Convert.FromHexString(fieldSection), out _), Is.False);
}
