// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Text;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class Eth2MessageIdTests
{
    private const string Topic = "/eth2/8c9f62fe/beacon_block/ssz_snappy";

    // Expected ids hand-derived with Python (hashlib + manually encoded snappy block framing):
    // SHA256(domain ++ uint64_le(38) ++ topic ++ payload)[:20] where payload is the snappy
    // decompression of the data for the valid domain (0x01000000) and the raw data otherwise.
    [TestCase("0x051068656c6c6f", "0xd58fc6bfe68c6db632dfb39698a85bdf1ff5975a", TestName = "valid snappy ('hello')")]
    [TestCase("0xffffffff", "0xbaeefcde2929c18a17edafb95a98bd25d7faf18f", TestName = "invalid snappy (truncated varint)")]
    [TestCase("0x05106865", "0x24ffdda03b98a8f19348882134a3040afb9a29c8", TestName = "invalid snappy (truncated literal)")]
    [TestCase("0x8080c0051068656c6c6f", "0x2f2d09180ea8448bbcad0d5d83d0b818a0dba8c5", TestName = "oversized declared length (11 MiB) uses the invalid domain")]
    public void Computes_known_vectors(string dataHex, string expectedIdHex)
    {
        byte[] id = Eth2MessageId.Compute(Topic, Bytes.FromHexString(dataHex));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(id, Has.Length.EqualTo(20));
            Assert.That(id, Is.EqualTo(Bytes.FromHexString(expectedIdHex)));
        }
    }

    [Test]
    public void Matches_independent_implementation_for_a_round_tripped_payload()
    {
        byte[] payload = new byte[300];
        new Random(42).NextBytes(payload);
        byte[] compressed = Snappy.CompressToArray(payload);

        // Independent implementation: plain concatenation hashed in one shot.
        byte[] topicBytes = Encoding.ASCII.GetBytes(Topic);
        byte[] preimage = [0x01, 0x00, 0x00, 0x00, (byte)topicBytes.Length, 0, 0, 0, 0, 0, 0, 0, .. topicBytes, .. payload];
        byte[] expected = SHA256.HashData(preimage)[..20];

        Assert.That(Eth2MessageId.Compute(Topic, compressed), Is.EqualTo(expected));
    }

    [TestCase("0x051068656c6c6f", SnappyDecodeResult.Decoded, "0x68656c6c6f", TestName = "decodes valid block data")]
    [TestCase("0xffffffff", SnappyDecodeResult.Invalid, null, TestName = "rejects corrupt data")]
    [TestCase("0x8080c0051068656c6c6f", SnappyDecodeResult.Oversized, null, TestName = "rejects an oversized declared length without decompressing")]
    public void Capped_decompression_reports_the_outcome(string dataHex, SnappyDecodeResult expected, string? decompressedHex)
    {
        SnappyDecodeResult result = Eth2MessageId.TryDecompress(Bytes.FromHexString(dataHex), Eth2MessageId.MaxGossipSize, out byte[]? decompressed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(decompressed, Is.EqualTo(decompressedHex is null ? null : Bytes.FromHexString(decompressedHex)));
        }
    }
}
