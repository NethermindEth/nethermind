// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// The ENR <c>cgc</c> byte-encoding rule (p2p-interface.md): "Uint64 big endian integer with no
/// leading zero bytes (0 encoded as empty byte string)". Each expected byte sequence below is
/// hand-derived from that rule and from RLP's own well-known encoding, independent of
/// <see cref="CustodyGroupCountEntry"/>'s implementation.
/// </summary>
public class CustodyGroupCountEntryTests
{
    [Test]
    public void Key_is_cgc() =>
        Assert.That(new CustodyGroupCountEntry(4).Key, Is.EqualTo("cgc"));

    [TestCase(0ul, new byte[] { 0x80 })] // 0 => empty byte string
    [TestCase(4ul, new byte[] { 0x04 })] // single byte < 0x80 encodes as itself
    [TestCase(127ul, new byte[] { 0x7F })]
    [TestCase(128ul, new byte[] { 0x81, 0x80 })] // >= 0x80 needs a length-1 string prefix
    [TestCase(256ul, new byte[] { 0x82, 0x01, 0x00 })] // big-endian, no leading zero byte
    [TestCase(ulong.MaxValue, new byte[] { 0x88, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void Value_encodes_as_the_exact_cgc_byte_rule(ulong value, byte[] expectedValueRlp)
    {
        CustodyGroupCountEntry entry = new(value);
        int keyLength = Rlp.LengthOf(entry.Key);

        byte[] buffer = new byte[keyLength + expectedValueRlp.Length + 8];
        RlpWriter writer = new(buffer);
        entry.Encode(ref writer);

        byte[] actualValueRlp = buffer[keyLength..writer.Position];

        Assert.That(actualValueRlp, Is.EqualTo(expectedValueRlp));
    }

    [Test]
    public void Value_matches_the_shared_rlp_ulong_encoder()
    {
        // Cross-check against the codebase's own independent general-purpose RLP integer encoder,
        // not against this entry's own output.
        foreach (ulong value in new[] { 0ul, 1ul, 4ul, 127ul, 128ul, 65535ul, ulong.MaxValue })
        {
            CustodyGroupCountEntry entry = new(value);
            int keyLength = Rlp.LengthOf(entry.Key);
            byte[] expected = Rlp.Encode(value).Bytes;

            byte[] buffer = new byte[keyLength + expected.Length + 8];
            RlpWriter writer = new(buffer);
            entry.Encode(ref writer);

            Assert.That(buffer[keyLength..writer.Position], Is.EqualTo(expected), $"value {value}");
        }
    }
}
