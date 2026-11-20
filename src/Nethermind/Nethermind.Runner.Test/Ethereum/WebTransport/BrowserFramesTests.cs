// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Runner.Ethereum.WebTransport;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Runner.Test.Ethereum.WebTransport;

public class BrowserFramesTests
{
    private const string NonceHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string NonceItem = "\"ENR-key-proof-v1=" + NonceHex + "\"";

    [TestCase("\"webtx/1\", " + NonceItem, BrowserProtocol.TxMode)]
    [TestCase(NonceItem + ", \"webdisc/1\"", BrowserProtocol.DiscoveryMode)]
    [TestCase(" \t\"webdisc/1\", \"webtx/1\",\t" + NonceItem + "  ", BrowserProtocol.TxMode)]
    [TestCase("\"unknown, \\\"protocol\\\"\", \"webdisc/1\";x=\"ignored,\";q=?1, " + NonceItem, BrowserProtocol.DiscoveryMode)]
    [TestCase("\"webtx/1\";size=2.31;date=@1234;binary=:YWJj:;text=%\"%c3%a9\";ref=A:/, " + NonceItem, BrowserProtocol.TxMode)]
    public void Parses_structured_protocol_offers(string header, byte expectedMode)
    {
        (byte Mode, byte[] Nonce)? offer = BrowserFrames.ParseOffer(header);
        Assert.That(offer.HasValue, Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(offer.Value.Mode, Is.EqualTo(expectedMode));
            Assert.That(offer.Value.Nonce, Is.EqualTo(Convert.FromHexString(NonceHex)));
        }
    }

    [Test]
    public void Rejects_invalid_or_incomplete_offers([Values(
        "",
        "\"webtx/1\"",
        "\"unknown\", " + NonceItem,
        "webtx/1, " + NonceItem,
        "\"webtx/1\", \"ENR-key-proof-v1=0123\"",
        "\"webtx/1\", \"ENR-key-proof-v1=z123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"",
        "\"webtx/1\", " + NonceItem + ", " + NonceItem,
        "\"webtx/1\", " + NonceItem + ",",
        "\"webtx/1\";x=, " + NonceItem,
        "\"webtx/1\";x=?2, " + NonceItem,
        "\"webtx/1\";x=%\"%ff\", " + NonceItem,
        "\"webtx/1\";x=:YWJj\r:, " + NonceItem,
        "\"webtx/1\";ref=/, " + NonceItem,
        "\"webtx/1\"\r\n, " + NonceItem)] string offer) =>
        Assert.That(BrowserFrames.ParseOffer(offer), Is.Null);

    [Test]
    public async Task Reads_transaction_frames_up_to_the_decoded_limit([Values(1, BrowserProtocol.MaxSubmitSize)] int length)
    {
        byte[] payload = new byte[length];
        new Random(19).NextBytes(payload);
        using MemoryStream stream = new(FrameRequest(payload));

        (IMemoryOwner<byte> owner, int decodedLength) = await BrowserFrames.ReadRequestAsync(stream, BrowserProtocol.TxMode, CancellationToken.None);

        using (owner)
        {
            Assert.That(owner.Memory[..decodedLength].ToArray(), Is.EqualTo(payload));
        }
    }

    [Test]
    public async Task Reads_the_discovery_empty_list_request()
    {
        using MemoryStream stream = new([0x10, 0, 0, 3, 1, 0, 0xc0]);

        (IMemoryOwner<byte> owner, int decodedLength) = await BrowserFrames.ReadRequestAsync(stream, BrowserProtocol.DiscoveryMode, CancellationToken.None);

        using (owner)
        {
            Assert.That(owner.Memory[..decodedLength].ToArray(), Is.EqualTo(new byte[] { 0xc0 }));
        }
    }

    [TestCaseSource(nameof(InvalidFrames))]
    public void Rejects_malformed_or_oversized_frames(byte mode, byte[] request)
    {
        using MemoryStream stream = new(request);
        Assert.That(async () => await BrowserFrames.ReadRequestAsync(stream, mode, CancellationToken.None), Throws.TypeOf<InvalidDataException>());
    }

    private static IEnumerable<TestCaseData> InvalidFrames()
    {
        yield return new TestCaseData(BrowserProtocol.TxMode, new byte[] { 0x11, 0, 0, 3, 1, 0, 0xc0 }).SetName("Read rejects the response code on a request");
        yield return new TestCaseData(BrowserProtocol.TxMode, new byte[] { 0x10, 0, 0, 0 }).SetName("Read rejects an empty compressed block");
        yield return new TestCaseData(BrowserProtocol.TxMode, new byte[] { 0x10, 0xff, 0xff, 0xff }).SetName("Read bounds the compressed frame length before reading its body");
        yield return new TestCaseData(BrowserProtocol.TxMode, new byte[] { 0x10, 0, 0, 1, 0 }).SetName("Read rejects a block declaring empty output");
        yield return new TestCaseData(BrowserProtocol.TxMode, new byte[] { 0x10, 0, 0, 5, 1, 0, 0xc0, 0, 0xc0 }).SetName("Read rejects trailing Snappy commands");
        yield return new TestCaseData(BrowserProtocol.TxMode, FrameRequest(new byte[BrowserProtocol.MaxSubmitSize + 1])).SetName("Read rejects decompressed data above the transaction limit");
        yield return new TestCaseData(BrowserProtocol.DiscoveryMode, new byte[] { 0x10, 0, 0, 3, 1, 0, 0x80 }).SetName("Read requires an empty RLP list for discovery");
    }

    [Test]
    public void Rejects_a_truncated_body()
    {
        using MemoryStream stream = new([0x10, 0, 0, 3, 1, 0]);
        Assert.That(async () => await BrowserFrames.ReadRequestAsync(stream, BrowserProtocol.TxMode, CancellationToken.None), Throws.TypeOf<EndOfStreamException>());
    }

    [Test]
    public async Task Writes_the_exact_response_frame()
    {
        using MemoryStream stream = new();

        await BrowserFrames.WriteResponseAsync(stream, [0xc2, 0xc1, 0x80], CancellationToken.None);

        Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0x11, 0, 0, 5, 3, 8, 0xc2, 0xc1, 0x80 }));
    }

    private static byte[] FrameRequest(byte[] payload)
    {
        byte[] encoded = Snappy.CompressToArray(payload);
        return [0x10, (byte)(encoded.Length >> 16), (byte)(encoded.Length >> 8), (byte)encoded.Length, .. encoded];
    }
}
