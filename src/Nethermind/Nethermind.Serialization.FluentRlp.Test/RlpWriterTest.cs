// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using FluentAssertions;
using Nethermind.Serialization.FluentRlp.Instances;

namespace Nethermind.Serialization.FluentRlp.Test;

[Parallelizable(ParallelScope.All)]
public class RlpWriterTest
{
    [Test]
    public void WriteShortString()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write("dog"); });

        byte[] expected = [0x83, (byte)'d', (byte)'o', (byte)'g'];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteEmptyString()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write(""); });

        byte[] expected = [0x80];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteLongString()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.Write("Lorem ipsum dolor sit amet, consectetur adipisicing elit");
        });

        byte[] expected = [0xb8, 0x38, .. "Lorem ipsum dolor sit amet, consectetur adipisicing elit"u8];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteZero()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write(0); });

        byte[] expected = [0x80];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteInteger_1Component()
    {
        for (int i = 1; i < 0x80; i++)
        {
            var integer = i;
            var serialized = Rlp.Write(integer, static (ref RlpWriter w, int integer) => { w.Write(integer); });

            byte[] expected = [(byte)integer];
            serialized.Should().BeEquivalentTo(expected);
        }
    }

    [Test]
    public void WriteInteger_2Components()
    {
        byte[] expected = [0x81, 0x00];
        for (int i = 0x80; i < 0x0100; i++)
        {
            var integer = i;
            var serialized = Rlp.Write(integer, static (ref RlpWriter w, int integer) => { w.Write(integer); });

            expected[1] = (byte)integer;
            serialized.Should().BeEquivalentTo(expected);
        }
    }

    [Test]
    public void WriteInteger_3Components()
    {
        byte[] expected = [0x82, 0x00, 0x00];
        for (int i = 0x100; i < 0xFFFF; i++)
        {
            var integer = i;
            var serialized = Rlp.Write(integer, static (ref RlpWriter w, int integer) => { w.Write(integer); });

            expected[1] = (byte)((integer & 0xFF00) >> 8);
            expected[2] = (byte)((integer & 0x00FF) >> 0);
            serialized.Should().BeEquivalentTo(expected);
        }
    }

    [Test]
    public void WriteStringList()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("cat");
                w.Write("dog");
            });
        });

        byte[] expected = [0xc8, 0x83, (byte)'c', (byte)'a', (byte)'t', 0x83, (byte)'d', (byte)'o', (byte)'g'];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteEmptyList()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });

        byte[] expected = [0xc0];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteSpan()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write([0x04, 0x00]); });

        byte[] expected = [0x82, 0x04, 0x00];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteSetTheoreticalRepresentation()
    {
        var serialized = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.WriteSequence(static (ref RlpWriter _) => { });
                w.WriteSequence(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });
                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.WriteSequence(static (ref RlpWriter _) => { });
                    w.WriteSequence(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });
                });
            });
        });

        byte[] expected = [0xc7, 0xc0, 0xc1, 0xc0, 0xc3, 0xc0, 0xc1, 0xc0];
        serialized.Should().BeEquivalentTo(expected);
    }
}
