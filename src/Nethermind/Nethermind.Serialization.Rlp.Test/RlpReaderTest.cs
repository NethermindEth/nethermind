// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Serialization.Rlp.Test.Instances;

namespace Nethermind.Serialization.Rlp.Test;

public class RlpReaderTest
{
    [Test]
    public void ReadShortString()
    {
        byte[] source = [0x83, (byte)'d', (byte)'o', (byte)'g'];

        var reader = new RlpReader(source);
        string actual = reader.ReadString();

        actual.Should().Be("dog");
    }

    [Test]
    public void ReadEmptyString()
    {
        byte[] source = [0x80];

        var reader = new RlpReader(source);
        string actual = reader.ReadString();

        actual.Should().Be("");
    }

    [Test]
    public void ReadLongString()
    {
        byte[] source = [0xb8, 0x38, .."Lorem ipsum dolor sit amet, consectetur adipisicing elit"u8];

        var reader = new RlpReader(source);
        string actual = reader.ReadString();

        actual.Should().Be("Lorem ipsum dolor sit amet, consectetur adipisicing elit");
    }

    [Test]
    public void ReadShortInteger()
    {
        for (int i = 0; i < 0x80; i++)
        {
            var integer = i;
            byte[] source = [(byte)integer];

            var reader = new RlpReader(source);
            int actual = reader.ReadInt32();

            actual.Should().Be(integer);
        }
    }

    [Test]
    public void ReadLongInteger()
    {
        for (int i = 0x100; i < 0xFFFF; i++)
        {
            var integer = i;
            byte[] source = [0x82, (byte)((integer & 0xFF00) >> 8), (byte)((integer & 0x00FF) >> 0)];

            var reader = new RlpReader(source);
            int actual = reader.ReadInt32();

            actual.Should().Be(integer);
        }
    }

    [Test]
    public void ReadStringList()
    {
        byte[] source = [0xc8, 0x83, .."cat"u8, 0x83, .."dog"u8];

        var reader = new RlpReader(source);
        var actual = reader.ReadList(static (ref RlpReader r) =>
        {
            var cat = r.ReadString();
            var dog = r.ReadString();

            return (cat, dog);
        });

        actual.Should().Be(("cat", "dog"));
    }

    [Test]
    public void ReadSetTheoreticalRepresentation()
    {
        byte[] source = [0xc7, 0xc0, 0xc1, 0xc0, 0xc3, 0xc0, 0xc1, 0xc0];

        var reader = new RlpReader(source);
        object[] actual = reader.ReadList(static (ref RlpReader r) =>
        {
            var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
            var _2 = r.ReadList(static (ref RlpReader r) =>
            {
                var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
                return new object[] { _1 };
            });
            var _3 = r.ReadList(static (ref RlpReader r) =>
            {
                var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
                var _2 = r.ReadList(static (ref RlpReader r) =>
                {
                    var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
                    return new object[] { _1 };
                });

                return new object[] { _1, _2 };
            });

            return new object[] { _1, _2, _3 };
        });

        actual.Should().BeEquivalentTo(new object[]
        {
            new object[] { },
            new object[] { new object[] { } },
            new object[] { new object[] { }, new object[] { new object[] { } } },
        });
    }

    [Test]
    public void ReadEmptyList()
    {
        byte[] source = [0xc0];

        var reader = new RlpReader(source);
        object[] actual = reader.ReadList((ref RlpReader _) => Array.Empty<object>());

        actual.Should().BeEmpty();
    }
}
