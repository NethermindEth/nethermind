// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class BencodeTests
{
    [Test]
    public void Decode_rejects_excessive_nesting_depth()
    {
        StringBuilder builder = new();
        for (int i = 0; i < 130; i++)
        {
            builder.Append('l');
        }

        for (int i = 0; i < 130; i++)
        {
            builder.Append('e');
        }

        byte[] payload = Encoding.ASCII.GetBytes(builder.ToString());

        Assert.That(() => BencodeDocument.Decode(payload), Throws.TypeOf<FormatException>());
    }

    [TestCase("i01e")]
    [TestCase("i-0e")]
    [TestCase("i+1e")]
    [TestCase("01:a")]
    [TestCase("d1:ai1e1:ai2ee")]
    public void Decode_canonical_rejects_noncanonical_values(string encoded)
        => Assert.That(() => BencodeDocument.Decode(Encoding.ASCII.GetBytes(encoded), requireCanonical: true), Throws.TypeOf<FormatException>());

    [Test]
    public void Decode_canonical_accepts_binary_dictionary_key()
    {
        byte[] value = [(byte)'d', (byte)'1', (byte)':', 0xff, (byte)'i', (byte)'1', (byte)'e', (byte)'e'];

        Assert.DoesNotThrow(() => BencodeDocument.Decode(value, requireCanonical: true));
        Assert.DoesNotThrow(() => DhtMutableItem.Sign(new byte[32], value, 1));
    }

    [Test]
    public void Encode_sorts_dictionary_keys_by_utf8_bytes()
    {
        BDictionary value = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("\U00010000", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("\uE000", Bencode.Integer(2)));

        byte[] encoded = Bencode.Encode(value);
        BDictionary decoded = BencodeDocument.Decode(encoded, requireCanonical: true).Root.AsDictionary("dictionary");

        Assert.That(decoded["\uE000"].AsInteger("first"), Is.EqualTo(2));
        Assert.That(encoded[1], Is.EqualTo((byte)'3'), "U+E000 must sort before U+10000 by UTF-8 bytes");
    }
}
