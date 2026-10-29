// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Serialization.FluentRlp.Test;

/// <summary>Tests the reads that callers use on purpose for stored or extensible formats.</summary>
public class RlpReaderOptInTest
{
    [TestCase("c3010203", 3, ExpectedResult = 3)]
    [TestCase("c0", 0, ExpectedResult = 0)]
    public int CountItemsWithinLimit(string hex, int maxCount)
        => RlpCodec.Read(Convert.FromHexString(hex), (scoped ref RlpReader r) =>
        {
            RlpReader items = r.EnterSequence(out int length);
            int count = items.CountItems(maxCount);
            r.ExitSequenceIgnoringRest(in items, length);
            return count;
        });

    [Test]
    public void CountItemsOverLimit()
        => Assert.That(static () => RlpCodec.Read(Convert.FromHexString("c3010203"), static (scoped ref RlpReader r) =>
        {
            RlpReader items = r.EnterSequence(out int length);
            return items.CountItems(2);
        }), Throws.TypeOf<RlpLimitException>());

    [TestCase("c0", true, false)]
    [TestCase("80", false, true)]
    [TestCase("01", false, false)]
    [TestCase("", false, false)]
    public void TryReadEmpty(string hex, bool isEmptySequence, bool isEmptyBytes)
    {
        byte[] source = Convert.FromHexString(hex);
        RlpReader sequenceReader = new(source);
        RlpReader bytesReader = new(source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sequenceReader.TryReadEmptySequence(), Is.EqualTo(isEmptySequence));
            Assert.That(sequenceReader.BytesRead, Is.EqualTo(isEmptySequence ? 1 : 0));
            Assert.That(bytesReader.TryReadEmptyBytes(), Is.EqualTo(isEmptyBytes));
            Assert.That(bytesReader.BytesRead, Is.EqualTo(isEmptyBytes ? 1 : 0));
        }
    }

    [TestCase("c20102", true)]
    [TestCase("820102", false)]
    [TestCase("01", false)]
    public void IsNextSequence(string hex, bool expected)
        => Assert.That(new RlpReader(Convert.FromHexString(hex)).IsNextSequence, Is.EqualTo(expected));

    [Test]
    public void IsNextSequenceAtEnd()
        => Assert.That(static () => new RlpReader([]).IsNextSequence, Throws.TypeOf<RlpReaderException>());

    [Test]
    public void ReadEncodedItemKeepsThePrefix()
    {
        byte[] source = Convert.FromHexString("c2c18183646f67");
        (string first, string second) = RlpCodec.Read(source, static (scoped ref RlpReader r) =>
            (Convert.ToHexString(r.ReadEncodedItem()), Convert.ToHexString(r.ReadEncodedItem())));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo("C2C181"));
            Assert.That(second, Is.EqualTo("83646F67"));
        }
    }

    [TestCase("8100", TestName = "Single byte with a prefix")]
    [TestCase("b801", TestName = "Long form of a short length")]
    [TestCase("c3", TestName = "Truncated sequence")]
    public void SkipItemRejectsMalformedPrefix(string hex)
        => Assert.That(() => RlpCodec.Read(Convert.FromHexString(hex), static (scoped ref RlpReader r) =>
        {
            r.SkipItem();
            return 0;
        }), Throws.TypeOf<RlpReaderException>());

    // EIP-8 messages can have items that a newer version added after the known ones.
    [Test]
    public void ExitSequenceIgnoringRestSkipsLaterItems()
    {
        byte[] source = Convert.FromHexString("c60102c3040506");
        (int a, int b) = RlpCodec.Read(source, static (scoped ref RlpReader r) =>
        {
            RlpReader items = r.EnterSequence(out int length);
            (int, int) result = (items.ReadInteger<int>(), items.ReadInteger<int>());
            r.ExitSequenceIgnoringRest(in items, length);
            return result;
        });

        Assert.That((a, b), Is.EqualTo((1, 2)));
    }

    [Test]
    public void ExitSequenceIgnoringRestChecksLaterPrefixes()
        => Assert.That(static () => RlpCodec.Read(Convert.FromHexString("c30181" + "00"), static (scoped ref RlpReader r) =>
        {
            RlpReader items = r.EnterSequence(out int length);
            int first = items.ReadInteger<int>();
            r.ExitSequenceIgnoringRest(in items, length);
            return first;
        }), Throws.TypeOf<RlpReaderException>());

    [Test]
    public void ReadRawAndTruncation()
    {
        byte[] source = [0x7f, 0xc1, 0x80];
        RlpReader reader = new(source);
        byte[] raw = reader.ReadRaw(2).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raw, Is.EqualTo(new byte[] { 0x7f, 0xc1 }));
            Assert.That(static () => new RlpReader([0x01]).ReadRaw(2), Throws.TypeOf<RlpReaderException>());
            Assert.That(static () => new RlpReader([0x01]).ReadRaw(-1), Throws.TypeOf<RlpReaderException>());
        }
    }

    [Test]
    public void ReadLeadingIgnoresTrailingBytes()
    {
        int value = RlpCodec.ReadLeading(Convert.FromHexString("05ffff"), static (scoped ref RlpReader r) => r.ReadInteger<int>(), out int bytesRead);

        Assert.That((value, bytesRead), Is.EqualTo((5, 1)));
    }

    [Test]
    public void WriteEncodedCopiesItems()
    {
        byte[] item = Convert.FromHexString("c2c180");
        byte[] rlp = RlpCodec.Write(item, static (ref RlpWriter w, byte[] item) => w.WriteSequence(item, static (ref RlpWriter w, byte[] item) => w.WriteEncoded(item)));

        Assert.That(Convert.ToHexString(rlp), Is.EqualTo("C3C2C180"));
    }
}
