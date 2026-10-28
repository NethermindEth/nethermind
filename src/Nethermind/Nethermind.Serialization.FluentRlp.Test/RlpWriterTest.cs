// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Nethermind.Serialization.FluentRlp.Instances;

namespace Nethermind.Serialization.FluentRlp.Test;

[Parallelizable(ParallelScope.All)]
public class RlpWriterTest
{
    [Test]
    public void WriteShortString()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write("dog"); });

        byte[] expected = [0x83, (byte)'d', (byte)'o', (byte)'g'];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public void WriteEmptyString()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write(""); });

        byte[] expected = [0x80];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public void WriteLongString()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.Write("Lorem ipsum dolor sit amet, consectetur adipisicing elit");
        });

        byte[] expected = [0xb8, 0x38, .. "Lorem ipsum dolor sit amet, consectetur adipisicing elit"u8];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    // A 55-byte payload is the largest that RLP encodes with a single-byte prefix; 56 is the smallest
    // that must use the long form. Straddling the boundary catches an off-by-one in either direction.
    [TestCase(54, (byte)0xb6)]
    [TestCase(55, (byte)0xb7)]
    [Test]
    public void WriteStringAtShortFormBoundary(int length, byte expectedPrefix)
    {
        byte[] serialized = Rlp.Write(length, static (ref RlpWriter w, int length) => w.Write(new string('A', length)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized[0], Is.EqualTo(expectedPrefix));
            Assert.That(serialized, Has.Length.EqualTo(1 + length));
        }
    }

    [Test]
    public void WriteStringJustOverShortFormBoundary()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => w.Write(new string('A', 56)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized[0], Is.EqualTo((byte)0xb8));
            Assert.That(serialized[1], Is.EqualTo((byte)56));
            Assert.That(serialized, Has.Length.EqualTo(2 + 56));
        }
    }

    [TestCase(54, (byte)0xf6)]
    [TestCase(55, (byte)0xf7)]
    [Test]
    public void WriteSequenceAtShortFormBoundary(int contentLength, byte expectedPrefix)
    {
        // Each element is a single byte below 0x80, so it encodes as itself.
        byte[] serialized = Rlp.Write(contentLength, static (ref RlpWriter w, int contentLength) =>
            w.WriteSequence(contentLength, static (ref RlpWriter w, int contentLength) =>
            {
                for (int i = 0; i < contentLength; i++)
                {
                    w.Write(1);
                }
            }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized[0], Is.EqualTo(expectedPrefix));
            Assert.That(serialized, Has.Length.EqualTo(1 + contentLength));
        }
    }

    [Test]
    public void WriteZero()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write(0); });

        byte[] expected = [0x80];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public void WriteInteger_1Component()
    {
        for (int i = 1; i < 0x80; i++)
        {
            int integer = i;
            byte[] serialized = Rlp.Write(integer, static (ref RlpWriter w, int integer) => { w.Write(integer); });

            byte[] expected = [(byte)integer];
            Assert.That(serialized, Is.EqualTo(expected));
        }
    }

    [Test]
    public void WriteInteger_2Components()
    {
        byte[] expected = [0x81, 0x00];
        for (int i = 0x80; i < 0x0100; i++)
        {
            int integer = i;
            byte[] serialized = Rlp.Write(integer, static (ref RlpWriter w, int integer) => { w.Write(integer); });

            expected[1] = (byte)integer;
            Assert.That(serialized, Is.EqualTo(expected));
        }
    }

    [Test]
    public void WriteInteger_3Components()
    {
        byte[] expected = [0x82, 0x00, 0x00];
        for (int i = 0x100; i < 0xFFFF; i++)
        {
            int integer = i;
            byte[] serialized = Rlp.Write(integer, static (ref RlpWriter w, int integer) => { w.Write(integer); });

            expected[1] = (byte)((integer & 0xFF00) >> 8);
            expected[2] = (byte)((integer & 0x00FF) >> 0);
            Assert.That(serialized, Is.EqualTo(expected));
        }
    }

    [Test]
    public void WriteStringList()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("cat");
                w.Write("dog");
            });
        });

        byte[] expected = [0xc8, 0x83, (byte)'c', (byte)'a', (byte)'t', 0x83, (byte)'d', (byte)'o', (byte)'g'];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public void WriteEmptyList()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });

        byte[] expected = [0xc0];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public void WriteSpan()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => { w.Write([0x04, 0x00]); });

        byte[] expected = [0x82, 0x04, 0x00];
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public void WriteSetTheoreticalRepresentation()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) =>
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
        Assert.That(serialized, Is.EqualTo(expected));
    }

    // A single byte below 0x80 is its own encoding; any other byte string has a prefix.
    [TestCase("", "80")]
    [TestCase("00", "00")]
    [TestCase("61", "61")]
    [TestCase("7f", "7f")]
    [TestCase("80", "8180")]
    [TestCase("ff", "81ff")]
    [TestCase("0000", "820000")]
    public void WriteShortByteString(string valueHex, string expectedHex)
    {
        byte[] value = Convert.FromHexString(valueHex);
        byte[] serialized = Rlp.Write(value, static (ref RlpWriter w, byte[] value) => w.Write(value));

        Assert.That(Convert.ToHexString(serialized), Is.EqualTo(expectedHex).IgnoreCase);
    }

    [Test]
    public void WriteSingleCharacterString()
    {
        byte[] serialized = Rlp.Write(static (ref RlpWriter w) => w.Write("a"));

        Assert.That(serialized, Is.EqualTo(new byte[] { 0x61 }));
    }

    [Test]
    public void WriteNegativeInteger()
        => Assert.That(() => Rlp.Write(static (ref RlpWriter w) => w.Write(-1)), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void WriteToTooShortDestination()
        => Assert.That(static () =>
        {
            RlpWriter writer = RlpWriter.ContentWriter(new byte[2]);
            writer.Write("dog");
        }, Throws.ArgumentException);

    // A write action that swallows the exception of a nested sequence leaves that sequence without a prefix.
    [Test]
    public void WriteAfterSwallowedNestedException()
        => Assert.That(static () => Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                try
                {
                    w.WriteSequence(static (ref RlpWriter _) => throw new FormatException());
                }
                catch (FormatException)
                {
                }

                w.Write(1);
            });
        }), Throws.InvalidOperationException);

    [Test]
    public void WriteDeeplyNestedSequence()
    {
        Node tree = Node.Nested(1_000_000);

        Assert.That(() => Rlp.Write(tree, static (ref RlpWriter w, Node tree) => tree.Write(ref w)), Throws.TypeOf<InsufficientExecutionStackException>());
    }

    // The buffered writer moves long prefixes into place when it copies its buffer. Nested sequences that cross the
    // 55-byte boundary at several depths check that each insertion lands at the right place.
    [Test]
    public void WriteNestedSequencesAsReference([Range(0, 49)] int seed)
    {
        Node tree = Node.Random(new Random(seed), depth: 0);
        byte[] expected = Node.Encode(tree);

        byte[] buffered = Rlp.Write(tree, static (ref RlpWriter w, Node tree) => tree.Write(ref w));

        RlpWriter lengthWriter = RlpWriter.LengthWriter();
        tree.Write(ref lengthWriter);
        byte[] measured = new byte[lengthWriter.Length];
        RlpWriter contentWriter = RlpWriter.ContentWriter(measured);
        tree.Write(ref contentWriter);

        ArrayBufferWriter<byte> bufferWriter = new();
        int written = Rlp.Write(bufferWriter, tree, static (ref RlpWriter w, Node tree) => tree.Write(ref w));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffered, Is.EqualTo(expected));
            Assert.That(measured, Is.EqualTo(expected));
            Assert.That(contentWriter.Length, Is.EqualTo(expected.Length));
            Assert.That(written, Is.EqualTo(expected.Length));
            Assert.That(bufferWriter.WrittenSpan.ToArray(), Is.EqualTo(expected));
        }
    }

    // The reader accepts only canonical input, so any input that it accepts must encode again to the same bytes.
    // Other input must fail with RlpReaderException and never with another exception.
    [Test]
    public void AcceptedInputEncodesToTheSameBytes([Range(0, 499)] int seed)
    {
        Random random = new(seed);
        byte[] input = Node.Encode(Node.Random(random, depth: 1));
        if (seed % 5 == 0)
        {
            input = new byte[random.Next(1, 64)];
            random.NextBytes(input);
        }
        else
        {
            for (int i = random.Next(1, 4); i > 0 && input.Length > 0; i--)
            {
                input[random.Next(input.Length)] = (byte)random.Next(256);
            }
        }

        Node? decoded = null;
        try
        {
            decoded = Rlp.Read(input, Node.Read);
        }
        catch (RlpReaderException)
        {
        }

        if (decoded is not null) Assert.That(Node.Encode(decoded), Is.EqualTo(input));
    }

    /// <summary>A byte string, or a sequence of nodes, with a reference encoder that builds prefixes after the payload.</summary>
    private sealed class Node(byte[]? bytes, List<Node>? children)
    {
        private byte[]? Bytes { get; } = bytes;
        private List<Node>? Children { get; } = children;

        public static Node Nested(int depth)
        {
            Node node = new([], null);
            for (int i = 0; i < depth; i++)
            {
                node = new Node(null, [node]);
            }

            return node;
        }

        public static Node Random(Random random, int depth)
        {
            if (depth == 4 || random.Next(3) == 0)
            {
                byte[] bytes = new byte[random.Next(4) == 0 ? random.Next(2) : random.Next(300)];
                random.NextBytes(bytes);
                return new Node(bytes, null);
            }

            List<Node> children = [];
            int count = random.Next(8);
            for (int i = 0; i < count; i++)
            {
                children.Add(Random(random, depth + 1));
            }

            return new Node(null, children);
        }

        public void Write(ref RlpWriter writer)
        {
            if (Bytes is not null)
            {
                writer.Write(Bytes);
                return;
            }

            writer.WriteSequence(Children!, static (ref RlpWriter w, List<Node> children) =>
            {
                foreach (Node child in children)
                {
                    child.Write(ref w);
                }
            });
        }

        public static Node Read(scoped ref RlpReader reader) => reader.Choice(
            static (scoped ref RlpReader r) => r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<Node> children = [];
                while (r.HasNext)
                {
                    children.Add(Read(ref r));
                }

                return new Node(null, children);
            }),
            static (scoped ref RlpReader r) => new Node(r.ReadBytes().ToArray(), null));

        public static byte[] Encode(Node node)
        {
            if (node.Bytes is { Length: 1 } single && single[0] < 0x80) return single;

            byte[] payload = node.Bytes ?? node.Children!.SelectMany(Encode).ToArray();
            byte shortPrefix = node.Bytes is null ? (byte)0xc0 : (byte)0x80;
            if (payload.Length <= 55) return [(byte)(shortPrefix + payload.Length), .. payload];

            byte[] length = BitConverter.GetBytes(payload.Length).Reverse().SkipWhile(static b => b == 0).ToArray();
            return [(byte)(shortPrefix + 55 + length.Length), .. length, .. payload];
        }
    }
}
