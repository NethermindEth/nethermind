// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Nethermind.Serialization.FluentRlp.Instances;

namespace Nethermind.Serialization.FluentRlp.Test;

public class RlpReaderTest
{
    [TestCase(new byte[] { 0x83 }, "dog", TestName = "ReadShortString")]
    [TestCase(new byte[] { 0x80 }, "", TestName = "ReadEmptyString")]
    [TestCase(new byte[] { 0xb8, 0x38 }, "Lorem ipsum dolor sit amet, consectetur adipisicing elit", TestName = "ReadLongString")]
    public void ReadString(byte[] prefix, string expected)
    {
        byte[] source = [.. prefix, .. Encoding.UTF8.GetBytes(expected)];
        string actual = Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadString());

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ReadShortInteger()
    {
        for (int i = 1; i < 0x80; i++)
        {
            int integer = i;
            byte[] source = [(byte)integer];
            int actual = Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadInt32());

            Assert.That(actual, Is.EqualTo(integer));
        }

        byte[] zero = [0x80];
        Assert.That(Rlp.Read(zero, static (scoped ref RlpReader r) => r.ReadInt32()), Is.Zero);
    }

    [Test]
    public void ReadLongInteger()
    {
        for (int i = 0x100; i <= 0xFFFF; i++)
        {
            int integer = i;
            byte[] source = [0x82, (byte)((integer & 0xFF00) >> 8), (byte)((integer & 0x00FF) >> 0)];
            int actual = Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadInt32());

            Assert.That(actual, Is.EqualTo(integer));
        }
    }

    [Test]
    public void ReadStringList()
    {
        byte[] source = [0xc8, 0x83, .. "cat"u8, 0x83, .. "dog"u8];
        (string, string) actual = Rlp.Read(source, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                string cat = r.ReadString();
                string dog = r.ReadString();

                return (cat, dog);
            });
        });

        Assert.That(actual, Is.EqualTo(("cat", "dog")));
    }

    [Test]
    public void ReadEmptyList()
    {
        byte[] source = [0xc0];

        object[] actual = Rlp.Read(source, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader _) => Array.Empty<object>());
        });

        Assert.That(actual, Is.Empty);
    }

    [Test]
    public void ReadSpan()
    {
        byte[] source = [0x82, 0x04, 0x00];

        ReadOnlySpan<byte> actual = Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadBytes());

        ReadOnlySpan<byte> expected = [0x04, 0x00];
        Assert.That(actual.SequenceEqual(expected), Is.True);
    }

    [Test]
    public void ReadSetTheoreticalRepresentation()
    {
        byte[] source = [0xc7, 0xc0, 0xc1, 0xc0, 0xc3, 0xc0, 0xc1, 0xc0];

        object[] actual = Rlp.Read(source, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                object[] _1 = r.ReadSequence(static (scoped ref RlpReader _) => Array.Empty<object>());
                object[] _2 = r.ReadSequence(static (scoped ref RlpReader r) =>
                {
                    object[] _1 = r.ReadSequence(static (scoped ref RlpReader _) => Array.Empty<object>());
                    return new object[] { _1 };
                });
                object[] _3 = r.ReadSequence(static (scoped ref RlpReader r) =>
                {
                    object[] _1 = r.ReadSequence(static (scoped ref RlpReader _) => Array.Empty<object>());
                    object[] _2 = r.ReadSequence(static (scoped ref RlpReader r) =>
                    {
                        object[] _1 = r.ReadSequence(static (scoped ref RlpReader _) => Array.Empty<object>());
                        return new object[] { _1 };
                    });

                    return new object[] { _1, _2 };
                });

                return new object[] { _1, _2, _3 };
            });
        });

        Assert.That(actual, Is.EqualTo(new object[]
        {
            Array.Empty<object>(),
            new object[] { Array.Empty<object>() },
            new object[] { Array.Empty<object>(), new object[] { Array.Empty<object>() } },
        }));
    }

    [Test]
    public void ReadTrailingBytes()
    {
        byte[] source = [0x83, (byte)'d', (byte)'o', (byte)'g'];

        RlpReader reader = new(source);
        _ = reader.ReadString();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.HasNext, Is.False);
            Assert.That(reader.BytesRead, Is.EqualTo(source.Length));
        }
    }

    // A sequence whose reader stops short leaves undecoded items behind. Both the short and the long
    // prefix form must reject that, so the cases straddle the 55-byte content boundary.
    [Test]
    public void ReadSequenceLeavingItemsUnconsumed([Values(2, 100)] int itemCount)
    {
        byte[] rlp = WriteDogs(itemCount);

        Assert.That(() => Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) => r.ReadString());
        }), Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void ReadSequenceConsumingEveryItem([Values(2, 100)] int itemCount)
    {
        byte[] rlp = WriteDogs(itemCount);

        List<string> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<string> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.All.EqualTo("dog"));
            Assert.That(decoded, Has.Count.EqualTo(itemCount));
        }
    }

    public enum Shape { Integer, Bytes, Sequence }

    // Every case must throw RlpReaderException, never another exception type.
    [TestCase("", Shape.Bytes, TestName = "Empty input")]
    [TestCase("8161", Shape.Bytes, TestName = "Single byte below 0x80 with a prefix")]
    [TestCase("8100", Shape.Integer, TestName = "Zero byte with a prefix")]
    [TestCase("00", Shape.Integer, TestName = "Integer zero as a raw zero byte")]
    [TestCase("820001", Shape.Integer, TestName = "Integer with a leading zero byte")]
    [TestCase("850100000000", Shape.Integer, TestName = "Integer wider than Int32")]
    [TestCase("8480000000", Shape.Integer, TestName = "Integer above Int32.MaxValue")]
    [TestCase("b80180", Shape.Bytes, TestName = "Long string prefix for a length below 56")]
    [TestCase("f800", Shape.Sequence, TestName = "Long list prefix for a length below 56")]
    [TestCase("bc0100000000", Shape.Bytes, TestName = "Length of length above 4")]
    [TestCase("bbffffffff", Shape.Bytes, TestName = "Length above Int32.MaxValue")]
    [TestCase("bb7fffffff", Shape.Bytes, TestName = "Length of Int32.MaxValue past the end")]
    [TestCase("b8", Shape.Bytes, TestName = "Truncated long length")]
    [TestCase("83646f", Shape.Bytes, TestName = "Truncated string")]
    [TestCase("c30102", Shape.Sequence, TestName = "Truncated list")]
    [TestCase("c283616263", Shape.Sequence, TestName = "Item longer than its parent list")]
    [TestCase("c0", Shape.Integer, TestName = "List read as an integer")]
    [TestCase("80", Shape.Sequence, TestName = "String read as a list")]
    [TestCase("0102", Shape.Integer, TestName = "Trailing bytes after the item")]
    public void RejectMalformedInput(string hex, Shape shape)
    {
        byte[] source = Convert.FromHexString(hex);

        Assert.That(() => Read(source, shape), Throws.TypeOf<RlpReaderException>());
    }

    private static int Read(byte[] source, Shape shape) => shape switch
    {
        Shape.Integer => Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadInt32()),
        Shape.Bytes => Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadBytes().Length),
        _ => Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadSequence(ReadAllBytes)),
    };

    [Test]
    public void ReadLongLengthWithLeadingZero()
    {
        byte[] source = [0xb9, 0x00, 0x38, .. new byte[56]];

        Assert.That(() => Rlp.Read(source, static (scoped ref RlpReader r) => r.ReadBytes().Length), Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void ReadDeeplyNestedSequence()
    {
        byte[] source = NestedLists(1_000_000);

        Assert.That(() => Rlp.Read(source, ReadDepth), Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void CountItemsDoesNotMoveTheReader()
    {
        byte[] payload = [0x82, 0x61, 0x62, 0xc0, 0x05];
        byte[] truncated = [0x05, 0xb8, 0x38];

        RlpReader reader = new(payload);
        int count = reader.CountItems();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(reader.BytesRead, Is.Zero);
            Assert.That(() => new RlpReader(truncated).CountItems(), Throws.TypeOf<RlpReaderException>());
        }
    }

    private static int ReadAllBytes(scoped ref RlpReader r)
    {
        int count = 0;
        while (r.HasNext)
        {
            count += r.ReadBytes().Length;
        }

        return count;
    }

    private static int ReadDepth(scoped ref RlpReader r)
        => r.ReadSequence(static (scoped ref RlpReader r) => r.HasNext ? 1 + ReadDepth(ref r) : 0);

    /// <summary>Builds <paramref name="depth"/> lists, each one the only item of the one around it.</summary>
    private static byte[] NestedLists(int depth)
    {
        int[] payloadLengths = new int[depth];
        for (int i = depth - 2; i >= 0; i--)
        {
            payloadLengths[i] = PrefixLength(payloadLengths[i + 1]) + payloadLengths[i + 1];
        }

        byte[] result = new byte[PrefixLength(payloadLengths[0]) + payloadLengths[0]];
        int position = 0;
        foreach (int payloadLength in payloadLengths)
        {
            if (payloadLength <= 55)
            {
                result[position++] = (byte)(0xc0 + payloadLength);
                continue;
            }

            int lengthOfLength = PrefixLength(payloadLength) - 1;
            result[position++] = (byte)(0xf7 + lengthOfLength);
            for (int i = lengthOfLength - 1; i >= 0; i--)
            {
                result[position++] = (byte)(payloadLength >> (8 * i));
            }
        }

        return result;
    }

    private static int PrefixLength(int payloadLength)
        => payloadLength <= 55 ? 1 : payloadLength <= 0xff ? 2 : payloadLength <= 0xffff ? 3 : payloadLength <= 0xffffff ? 4 : 5;

    private static byte[] WriteDogs(int itemCount)
        => Rlp.Write(itemCount, static (ref RlpWriter w, int itemCount) =>
        {
            w.WriteSequence(itemCount, static (ref RlpWriter w, int itemCount) =>
            {
                for (int i = 0; i < itemCount; i++)
                {
                    w.Write("dog");
                }
            });
        });
}
