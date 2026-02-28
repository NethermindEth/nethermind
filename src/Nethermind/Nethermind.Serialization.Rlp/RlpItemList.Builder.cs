// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed partial class RlpItemList
{
    // Builds an RlpItemList from arbitrary nested structures in a single pass.
    public sealed class Builder : IDisposable
    {
        private readonly ArrayPoolList<Entry> _entries;
        private readonly ArrayPoolList<byte> _valueBuffer;

        public Builder(int entryCapacity = 16, int valueCapacity = 256)
        {
            _entries = new ArrayPoolList<Entry>(entryCapacity);
            _valueBuffer = new ArrayPoolList<byte>(valueCapacity);
            // Entry[0] is a virtual root that tracks total RLP size; it is not written to the output.
            _entries.Add(new Entry { IsLeaf = false, Length = 0, ValueOffset = 0 });
        }

        public Writer BeginRootContainer() => new Writer(this, 0, -1);

        public IRlpItemList ToRlpItemList()
        {
            Span<Entry> entries = _entries.AsSpan();
            int contentLength = entries[0].Length;
            int outerLength = Rlp.LengthOfSequence(contentLength);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(outerLength);
            Span<byte> buf = owner.Memory.Span;
            Span<byte> values = _valueBuffer.AsSpan();
            int pos = EncodeSequencePrefix(buf, 0, contentLength);

            for (int i = 1; i < entries.Length; i++)
            {
                ref Entry entry = ref entries[i];
                if (entry.IsLeaf)
                    pos = EncodeBytes(buf, pos, values.Slice(entry.ValueOffset, entry.Length));
                else
                    pos = EncodeSequencePrefix(buf, pos, entry.Length);
            }

            Memory<byte> region = owner.Memory.Slice(0, pos);
            return new RlpItemList(owner, region);
        }

        public void Dispose()
        {
            _entries.Dispose();
            _valueBuffer.Dispose();
        }

        private static int EncodeSequencePrefix(Span<byte> buf, int pos, int contentLength)
        {
            if (contentLength < 56)
            {
                buf[pos++] = (byte)(192 + contentLength);
            }
            else
            {
                int lengthOfLength = Rlp.LengthOfLength(contentLength);
                buf[pos++] = (byte)(247 + lengthOfLength);
                pos = WriteLength(buf, pos, contentLength);
            }

            return pos;
        }

        private static int EncodeBytes(Span<byte> buf, int pos, ReadOnlySpan<byte> input)
        {
            if (input.IsEmpty)
            {
                buf[pos++] = 128;
            }
            else if (input.Length == 1 && input[0] < 128)
            {
                buf[pos++] = input[0];
            }
            else if (input.Length < 56)
            {
                buf[pos++] = (byte)(128 + input.Length);
                input.CopyTo(buf.Slice(pos));
                pos += input.Length;
            }
            else
            {
                int lengthOfLength = Rlp.LengthOfLength(input.Length);
                buf[pos++] = (byte)(183 + lengthOfLength);
                pos = WriteLength(buf, pos, input.Length);
                input.CopyTo(buf.Slice(pos));
                pos += input.Length;
            }

            return pos;
        }

        private static int WriteLength(Span<byte> buf, int pos, int value)
        {
            if (value < 1 << 8)
            {
                buf[pos++] = (byte)value;
            }
            else if (value < 1 << 16)
            {
                buf[pos++] = (byte)(value >> 8);
                buf[pos++] = (byte)value;
            }
            else if (value < 1 << 24)
            {
                buf[pos++] = (byte)(value >> 16);
                buf[pos++] = (byte)(value >> 8);
                buf[pos++] = (byte)value;
            }
            else
            {
                buf[pos++] = (byte)(value >> 24);
                buf[pos++] = (byte)(value >> 16);
                buf[pos++] = (byte)(value >> 8);
                buf[pos++] = (byte)value;
            }

            return pos;
        }

        private struct Entry
        {
            public int Length;
            public int ValueOffset;
            public bool IsLeaf;
        }

        public ref struct Writer
        {
            private readonly Builder _builder;
            private readonly int _entryIndex;
            private readonly int _parentEntryIndex;

            internal Writer(Builder builder, int entryIndex, int parentEntryIndex)
            {
                _builder = builder;
                _entryIndex = entryIndex;
                _parentEntryIndex = parentEntryIndex;
            }

            public void WriteValue(ReadOnlySpan<byte> value)
            {
                Builder b = _builder;
                int offset = b._valueBuffer.Count;
                b._valueBuffer.AddRange(value);

                b._entries.Add(new Entry { IsLeaf = true, Length = value.Length, ValueOffset = offset });

                b._entries.AsSpan()[_entryIndex].Length += Rlp.LengthOf(value);
            }

            public Writer BeginContainer()
            {
                Builder b = _builder;
                int idx = b._entries.Count;
                b._entries.Add(new Entry { IsLeaf = false, Length = 0, ValueOffset = 0 });
                return new Writer(b, idx, _entryIndex);
            }

            public void Dispose()
            {
                if (_parentEntryIndex < 0) return;
                Builder b = _builder;
                Span<Entry> entries = b._entries.AsSpan();
                int seqLen = Rlp.LengthOfSequence(entries[_entryIndex].Length);
                entries[_parentEntryIndex].Length += seqLen;
            }
        }
    }
}
