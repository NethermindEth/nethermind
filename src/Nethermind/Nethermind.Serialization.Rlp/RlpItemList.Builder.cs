// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed partial class RlpItemList
{
    // Builds an RlpItemList from arbitrary nested structures in a single pass.
    public sealed class Builder : IDisposable
    {
        private ArrayPoolList<Entry>? _entries;
        private ArrayPoolList<byte>? _valueBuffer;
        private bool _rootWriterDisposed;

        public Builder(int entryCapacity = 16, int valueCapacity = 256)
        {
            _entries = new ArrayPoolList<Entry>(entryCapacity);
            _valueBuffer = new ArrayPoolList<byte>(valueCapacity);
            // Entry[0] is a virtual root that tracks total RLP size; it is not written to the output.
            _entries.Add(new Entry { Length = 0, ValueOffset = 0, EntriesLength = 0, ValueBufferLength = 0 });
        }

        public Writer BeginRootContainer() => new Writer(this, 0, -1);

        public IRlpItemList ToRlpItemList()
        {
            if (!_rootWriterDisposed) throw new InvalidOperationException("Root writer must be disposed before calling ToRlpItemList().");
            // Transfer ownership — Builder.Dispose() becomes no-op after this.
            BuilderRlpItemList view = new(_entries!, _valueBuffer!, entryStart: 0);
            _entries = null;
            _valueBuffer = null;
            return view;
        }

        public void Dispose()
        {
            _entries?.Dispose();
            _valueBuffer?.Dispose();
        }

        internal struct Entry
        {
            public int Length;           // Leaf: raw byte count. Container: RLP content length of children.
            public int ValueOffset;      // Leaf: offset into _valueBuffer. Container: starting _valueBuffer offset for subtree.
            public int EntriesLength;    // Leaf: -1. Container: total entry count in subtree (excl. self), >= 0.
            public int ValueBufferLength;// Leaf: same as Length. Container: total _valueBuffer bytes for subtree.
            public bool IsLeaf => EntriesLength < 0;
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
                int offset = b._valueBuffer!.Count;
                b._valueBuffer.AddRange(value);

                b._entries!.Add(new Entry { Length = value.Length, ValueOffset = offset, EntriesLength = -1, ValueBufferLength = value.Length });

                b._entries.AsSpan()[_entryIndex].Length += Rlp.LengthOf(value);
            }

            public Writer BeginContainer()
            {
                Builder b = _builder;
                int idx = b._entries!.Count;
                b._entries.Add(new Entry { Length = 0, ValueOffset = b._valueBuffer!.Count, EntriesLength = 0, ValueBufferLength = 0 });
                return new Writer(b, idx, _entryIndex);
            }

            public void Dispose()
            {
                Builder b = _builder;
                Span<Entry> entries = b._entries!.AsSpan();
                ref Entry self = ref entries[_entryIndex];
                self.EntriesLength = b._entries.Count - _entryIndex - 1;
                self.ValueBufferLength = b._valueBuffer!.Count - self.ValueOffset;
                if (_parentEntryIndex < 0) { b._rootWriterDisposed = true; return; }
                int seqLen = Rlp.LengthOfSequence(self.Length);
                entries[_parentEntryIndex].Length += seqLen;
            }
        }
    }
}
