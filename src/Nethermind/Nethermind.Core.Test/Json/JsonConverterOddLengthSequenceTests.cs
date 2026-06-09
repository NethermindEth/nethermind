// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class JsonConverterOddLengthSequenceTests
{
    // 63 hex digits (odd) — would be left-padded into a valid 32-byte hash if not rejected.
    private const string OddHash = "\"0x5102190bcfd53cc6a15761c9d2da43a628d0ca713bfad5a1e311b531c294b77\"";
    // 39 hex digits (odd) — would be left-padded into a valid 20-byte address if not rejected.
    private const string OddAddress = "\"0xc94770007dda54cf92009bff0de90c06f603a09\"";

    private static readonly JsonSerializerOptions Options = new();

    [Test]
    public void Hash256_rejects_odd_length_across_buffer_segments()
    {
        Exception? caught = null;
        try
        {
            Utf8JsonReader reader = ReaderOverTwoSegments(OddHash);
            Assert.That(reader.HasValueSequence, Is.True, "test must exercise the multi-segment path");
            new Hash256Converter().Read(ref reader, typeof(Hash256), Options);
        }
        catch (Exception ex) { caught = ex; }

        Assert.That(caught, Is.InstanceOf<FormatException>());
    }

    [Test]
    public void ValueHash256_rejects_odd_length_across_buffer_segments()
    {
        Exception? caught = null;
        try
        {
            Utf8JsonReader reader = ReaderOverTwoSegments(OddHash);
            Assert.That(reader.HasValueSequence, Is.True, "test must exercise the multi-segment path");
            new ValueHash256Converter().Read(ref reader, typeof(ValueHash256), Options);
        }
        catch (Exception ex) { caught = ex; }

        Assert.That(caught, Is.InstanceOf<FormatException>());
    }

    [Test]
    public void Address_rejects_odd_length_across_buffer_segments()
    {
        Exception? caught = null;
        try
        {
            Utf8JsonReader reader = ReaderOverTwoSegments(OddAddress);
            Assert.That(reader.HasValueSequence, Is.True, "test must exercise the multi-segment path");
            new AddressConverter().Read(ref reader, typeof(Address), Options);
        }
        catch (Exception ex) { caught = ex; }

        Assert.That(caught, Is.InstanceOf<FormatException>());
    }

    private static Utf8JsonReader ReaderOverTwoSegments(string json)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        int splitAt = bytes.Length / 2; // split inside the hex value so the string token straddles segments
        BufferSegment first = new(bytes.AsMemory(0, splitAt));
        BufferSegment last = first.Append(bytes.AsMemory(splitAt));
        Utf8JsonReader reader = new(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        reader.Read();
        return reader;
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            BufferSegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
