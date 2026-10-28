// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Walk;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Computes a trie root from strictly increasing hashed keys with bounded working memory.</summary>
internal sealed class SortedStateRoot : IDisposable
{
    private const int PathLength = Hash256.Size * 2;
    private readonly Frame?[] _frames = new Frame[PathLength];
    private ValueHash256 _previous;
    private NodeView _pending;
    private int _pendingDepth;
    private int _count;
    private bool _hasValue;
    private bool _isFinished;

    public void Add(in ValueHash256 key, ReadOnlySpan<byte> value)
    {
        if (_isFinished) throw new InvalidOperationException("The sorted trie has already finished.");
        if (value.IsEmpty) throw new ArgumentException("Omit deleted leaves from a sorted trie.", nameof(value));
        if (_hasValue)
        {
            if (key.Bytes.SequenceCompareTo(_previous.Bytes) <= 0)
                throw new InvalidDataException("Sorted trie keys must be strictly increasing.");

            int shared = 0;
            while (Nibble(key, shared) == Nibble(_previous, shared)) shared++;
            while (_count > 0 && _frames[_count - 1]!.Depth > shared) Collapse();
            if (_count == 0 || _frames[_count - 1]!.Depth < shared)
            {
                Frame frame = _frames[_count] ??= new Frame();
                frame.Depth = shared;
                _count++;
            }
            AttachPending();
        }

        _pending = NodeView.Leaf([], value);
        _pendingDepth = PathLength;
        _previous = key;
        _hasValue = true;
    }

    public ValueHash256 Finish()
    {
        if (_isFinished) throw new InvalidOperationException("The sorted trie has already finished.");
        _isFinished = true;
        if (!_hasValue) return Keccak.EmptyTreeHash.ValueHash256;
        while (_count > 0) Collapse();
        LiftPending(0);
        return _pending.Hash;
    }

    private void AttachPending()
    {
        Frame frame = _frames[_count - 1]!;
        LiftPending(frame.Depth + 1);
        int child = Nibble(_previous, frame.Depth);
        if (frame.Children[child].Kind != NodeViewKind.Empty)
            throw new InvalidDataException("A sorted trie child was visited twice.");
        frame.Children[child] = _pending;
        _pending = default;
    }

    private void Collapse()
    {
        AttachPending();
        Frame frame = _frames[_count - 1]!;
        _pending = NodeViews.Combine(frame.Children);
        _pendingDepth = frame.Depth;
        frame.Clear();
        _count--;
    }

    private void LiftPending(int depth)
    {
        if (depth == _pendingDepth) return;
        if (depth > _pendingDepth) throw new InvalidDataException("Invalid sorted trie depth.");
        Span<byte> path = stackalloc byte[PathLength];
        for (int index = depth; index < _pendingDepth; index++) path[index - depth] = (byte)Nibble(_previous, index);
        NodeView lifted = NodeViews.Prepend(path[..(_pendingDepth - depth)], _pending);
        _pending.Release();
        _pending = lifted;
        _pendingDepth = depth;
    }

    private static int Nibble(in ValueHash256 path, int depth) =>
        (depth & 1) == 0 ? path.Bytes[depth / 2] >> 4 : path.Bytes[depth / 2] & 15;

    public void Dispose()
    {
        _isFinished = true;
        _pending.Release();
        _pending = default;
        foreach (Frame? frame in _frames) frame?.Clear();
    }

    public void Reset()
    {
        Dispose();
        _count = 0;
        _pendingDepth = 0;
        _previous = default;
        _hasValue = false;
        _isFinished = false;
    }

    private sealed class Frame
    {
        public int Depth { get; set; }
        public NodeView[] Children { get; } = new NodeView[16];

        public void Clear()
        {
            for (int index = 0; index < Children.Length; index++)
            {
                Children[index].Release();
                Children[index] = default;
            }
        }
    }
}
