// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Collections;
using Entry = Nethermind.Serialization.Rlp.RlpItemList.Builder.Entry;

namespace Nethermind.Serialization.Rlp;

internal sealed class BuilderRlpItemList : IRlpItemList
{
    private readonly BuilderRlpItemList? _root;
    private ArrayPoolList<Entry>? _entries;
    private ArrayPoolList<byte>? _valueBuffer;
    private int _entryStart;
    private int _entryEnd;
    private int _count;

    private int _cachedIndex;
    private int _cachedEntryPos;

    private BuilderRlpItemList? _pooledChild;
    private BuilderRlpItemList? _parent;
    private bool _wasDisposed;

    // Root constructor — owns the arrays.
    internal BuilderRlpItemList(ArrayPoolList<Entry> entries, ArrayPoolList<byte> valueBuffer, int entryStart)
    {
        _root = null;
        _entries = entries;
        _valueBuffer = valueBuffer;
        _entryStart = entryStart;
        _entryEnd = entryStart + 1 + entries.AsSpan()[entryStart].EntriesLength;
        _count = -1;
        _cachedIndex = 0;
        _cachedEntryPos = entryStart + 1;
    }

    // Child constructor — borrows from root.
    private BuilderRlpItemList(BuilderRlpItemList root, int entryStart, int entryEnd, BuilderRlpItemList parent)
    {
        _root = root;
        _entries = null;
        _valueBuffer = null;
        _entryStart = entryStart;
        _entryEnd = entryEnd;
        _count = -1;
        _cachedIndex = 0;
        _cachedEntryPos = entryStart + 1;
        _parent = parent;
    }

    private Span<Entry> Entries => (_root?._entries ?? _entries)!.AsSpan();
    private Span<byte> ValueBuffer => (_root?._valueBuffer ?? _valueBuffer)!.AsSpan();

    public int Count
    {
        get
        {
            if (_count < 0) _count = ComputeCount();
            return _count;
        }
    }

    public int RlpLength => Rlp.LengthOfSequence(Entries[_entryStart].Length);

    public void Write(RlpStream stream)
    {
        Span<Entry> entries = Entries;
        Span<byte> values = ValueBuffer;
        stream.StartSequence(entries[_entryStart].Length);
        for (int i = _entryStart + 1; i < _entryEnd; i++)
        {
            ref Entry entry = ref entries[i];
            if (entry.IsLeaf)
                stream.Encode(values.Slice(entry.ValueOffset, entry.Length));
            else
                stream.StartSequence(entry.Length);
        }
    }

    public ReadOnlySpan<byte> ReadContent(int index)
    {
        Span<Entry> entries = Entries;
        int pos = GetDirectChildEntryIndex(entries, index);
        ref Entry entry = ref entries[pos];
        if (!entry.IsLeaf) throw new RlpException("Item is not a byte string");
        return ValueBuffer.Slice(entry.ValueOffset, entry.Length);
    }

    public IRlpItemList GetNestedItemList(int index)
    {
        Span<Entry> entries = Entries;
        int pos = GetDirectChildEntryIndex(entries, index);
        ref Entry entry = ref entries[pos];
        if (entry.IsLeaf) throw new RlpException("Item is not an RLP list");

        int childEnd = pos + 1 + entry.EntriesLength;

        BuilderRlpItemList? child = _pooledChild;
        if (child is not null)
        {
            _pooledChild = null;
            child.Reset(pos, childEnd);
            return child;
        }

        return new BuilderRlpItemList(_root ?? this, pos, childEnd, parent: this);
    }

    public RefRlpListReader CreateNestedReader(int index) =>
        throw new NotSupportedException("CreateNestedReader is not supported on BuilderRlpItemList");

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _wasDisposed, true, false)) return;

        if (_parent is not null)
        {
            if (!_parent._wasDisposed && _parent._pooledChild is null)
            {
                _parent._pooledChild = this;
                return;
            }
            // Parent already disposed or already has a pooled child — nothing to release.
            return;
        }

        // Root: dispose owned arrays.
        _pooledChild = null;
        _entries?.Dispose();
        _valueBuffer?.Dispose();
    }

    private void Reset(int entryStart, int entryEnd)
    {
        _entryStart = entryStart;
        _entryEnd = entryEnd;
        _count = -1;
        _cachedIndex = 0;
        _cachedEntryPos = entryStart + 1;
        _wasDisposed = false;
    }

    private int ComputeCount()
    {
        Span<Entry> entries = Entries;
        int count = 0;
        int pos = _entryStart + 1;
        while (pos < _entryEnd)
        {
            ref Entry entry = ref entries[pos];
            pos += 1 + (entry.IsLeaf ? 0 : entry.EntriesLength);
            count++;
        }
        return count;
    }

    private int GetDirectChildEntryIndex(Span<Entry> entries, int index)
    {
        int scanFrom;
        int pos;
        if (index >= _cachedIndex)
        {
            scanFrom = _cachedIndex;
            pos = _cachedEntryPos;
        }
        else
        {
            scanFrom = 0;
            pos = _entryStart + 1;
        }

        for (int i = scanFrom; i < index; i++)
        {
            ref Entry entry = ref entries[pos];
            pos += 1 + (entry.IsLeaf ? 0 : entry.EntriesLength);
        }

        _cachedIndex = index;
        _cachedEntryPos = pos;
        return pos;
    }
}
