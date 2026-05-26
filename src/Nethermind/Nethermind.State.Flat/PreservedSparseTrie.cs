// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Cross-block sparse trie preservation with ownership transfer protocol.
/// Shared across consecutive blocks via FlatScopeProvider. Take() transfers
/// exclusive ownership; Store*() returns it. CheckedOut state prevents double-take.
/// </summary>
public sealed class PreservedSparseTrie
{
    private readonly object _lock = new();
    private SparseStateTrie? _trie;
    private Hash256? _anchorStateRoot;
    private State _state = State.Empty;

    private enum State { Empty, Anchored, Cleared, CheckedOut }

    public SparseStateTrie Take(Hash256 parentStateRoot)
    {
        lock (_lock)
        {
            if (_state == State.CheckedOut)
                throw new InvalidOperationException("Sparse trie already checked out by another block");

            SparseStateTrie result;
            switch (_state)
            {
                case State.Anchored when _anchorStateRoot == parentStateRoot:
                    result = _trie!;
                    break;
                case State.Anchored:
                    _trie!.Clear();
                    result = _trie;
                    break;
                case State.Cleared:
                    result = _trie!;
                    break;
                default:
                    result = new SparseStateTrie();
                    break;
            }

            _trie = null;
            _anchorStateRoot = null;
            _state = State.CheckedOut;
            return result;
        }
    }

    public void StoreAnchored(SparseStateTrie trie, Hash256 stateRoot)
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut)
                throw new InvalidOperationException($"Expected CheckedOut, got {_state}");
            _trie = trie;
            _anchorStateRoot = stateRoot;
            _state = State.Anchored;
        }
    }

    public void StoreCleared(SparseStateTrie trie)
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut)
                throw new InvalidOperationException($"Expected CheckedOut, got {_state}");
            trie.Clear();
            _trie = trie;
            _anchorStateRoot = null;
            _state = State.Cleared;
        }
    }
}
