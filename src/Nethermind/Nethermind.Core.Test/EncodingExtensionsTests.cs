// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class EncodingExtensionsTests
{
    private class ReadOnlySequenceBuilder<T>
    {
        private ReadOnlyChunk<T>? _first;
        private ReadOnlyChunk<T>? _current;

        public ReadOnlySequenceBuilder()
        {
            _first = _current = null;
        }

        public ReadOnlySequenceBuilder<T> WithSegment(ReadOnlyMemory<T> memory)
        {
            if (_current == null) _first = _current = new(memory);
            else _current = _current.Append(memory);

            return this;
        }

        public ReadOnlySequenceBuilder<T> WithSegment(ReadOnlySequence<T> sequence)
        {
            SequencePosition pos = sequence.Start;
            while (sequence.TryGet(ref pos, out ReadOnlyMemory<T> mem))
                WithSegment(mem);
            return this;
        }

        public ReadOnlySequenceBuilder<T> WithSegment(T[] array) => WithSegment(array.AsMemory());

        public ReadOnlySequence<T> Build()
        {
            if (_first == null || _current == null) return new();
            return new(_first, 0, _current, _current.Memory.Length);
        }

        private sealed class ReadOnlyChunk<TT> : ReadOnlySequenceSegment<TT>
        {
            public ReadOnlyChunk(ReadOnlyMemory<TT> memory)
            {
                Memory = memory;
            }

            public ReadOnlyChunk<TT> Append(ReadOnlyMemory<TT> memory)
            {
                var nextChunk = new ReadOnlyChunk<TT>(memory)
                {
                    RunningIndex = RunningIndex + Memory.Length
                };

                Next = nextChunk;
                return nextChunk;
            }
        }
    }

    [Test]
    // 1-byte chars
    [TestCase("1234567890", 1)]
    [TestCase("1234567890", 5)]
    [TestCase("1234567890", 10)]
    [TestCase("1234567890", 20)]
    // JSON
    [TestCase("""{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}""", 10)]
    [TestCase("""{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}""", 63)]
    [TestCase("""{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}""", 64)]
    // 2-bytes chars
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 1)]
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 3)]
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 5)]
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 10)]
    public void TryGetStringSlice_Utf8_SingleSegment(string text, int charsLimit)
    {
        System.Text.Encoding encoding = System.Text.Encoding.UTF8;
        string expected = charsLimit > text.Length ? text : text[..charsLimit];
        var sequence = new ReadOnlySequence<byte>(encoding.GetBytes(text));

        encoding.TryGetStringSlice(sequence, charsLimit, out var completed, out var result).Should().BeTrue();

        result.Should().Be(expected);
        completed.Should().Be(charsLimit >= text.Length);
    }

    [Test]
    // 1-byte chars
    [TestCase(new byte[] { 0x31 }, new byte[] { 0x32, 0x33, 0x34, 0x35 }, 1)]
    [TestCase(new byte[] { 0x31, 0x32, 0x33 }, new byte[] { 0x34, 0x35 }, 5)]
    [TestCase(new byte[] { 0x31, 0x32, 0x33 }, new byte[] { 0x34, 0x35 }, 10)]
    // 2-bytes chars
    [TestCase(new byte[] { 0xc4 }, new byte[] { 0x81 }, 1)]
    [TestCase(new byte[] { 0xc4, 0x81, 0xc4, 0x82, 0xc4 }, new byte[] { 0x83, 0xc4, 0x84, 0xc4, 0x85 }, 3)]
    [TestCase(new byte[] { 0xc4, 0x81, 0xc4, 0x82, 0xc4 }, new byte[] { 0x83, 0xc4, 0x84, 0xc4, 0x85 }, 5)]
    [TestCase(new byte[] { 0xc4, 0x81, 0xc4, 0x82, 0xc4 }, new byte[] { 0x83, 0xc4, 0x84, 0xc4, 0x85 }, 10)]
    public void TryGetStringSlice_Utf8_MultiSegment(byte[] segment1, byte[] segment2, int charsLimit)
    {
        System.Text.Encoding encoding = System.Text.Encoding.UTF8;
        string text = encoding.GetString(segment1.Concat(segment2).ToArray());
        string expected = charsLimit > text.Length ? text : text[..charsLimit];
        ReadOnlySequence<byte> sequence = new ReadOnlySequenceBuilder<byte>()
            .WithSegment(new ReadOnlySequence<byte>(segment1))
            .WithSegment(new ReadOnlySequence<byte>(segment2))
            .Build();

        encoding.TryGetStringSlice(sequence, charsLimit, out var completed, out var result).Should().BeTrue();

        result.Should().Be(expected);
        completed.Should().Be(charsLimit >= text.Length);
    }
}
