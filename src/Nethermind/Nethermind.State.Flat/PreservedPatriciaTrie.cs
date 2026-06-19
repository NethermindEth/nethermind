// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.State;

namespace Nethermind.State.Flat;

/// <summary>
/// Retains a warmed Patricia account trie across consecutive flat-state scopes.
/// </summary>
public sealed class PreservedPatriciaTrie
{
    /// <summary>
    /// Repoints a preserved tree's trie store at the current scope resources.
    /// </summary>
    public delegate void Rebinder(SnapshotBundle bundle, ConcurrencyController quota);

    private readonly object _lock = new();
    private StateTree? _tree;
    private Rebinder? _rebind;
    private Hash256? _anchorStateRoot;
    private State _state = State.Empty;

    private enum State
    {
        Empty,
        Anchored,
        Cleared,
        CheckedOut
    }

    /// <summary>
    /// Checks out the preserved tree when its anchor matches the parent state root.
    /// </summary>
    /// <returns><c>true</c> when a tree was reused; otherwise <c>false</c>.</returns>
    public bool TryTake(Hash256 parentStateRoot, SnapshotBundle newBundle, ConcurrencyController newQuota, out StateTree tree)
    {
        lock (_lock)
        {
            if (_state == State.CheckedOut)
            {
                throw new InvalidOperationException("Preserved Patricia trie is already checked out by another scope.");
            }

            if (_state == State.Anchored && _anchorStateRoot == parentStateRoot && _tree is not null && _rebind is not null)
            {
                _rebind(newBundle, newQuota);
                tree = _tree;
                _tree = null;
                _anchorStateRoot = null;
                _state = State.CheckedOut;
                return true;
            }

            _tree = null;
            _rebind = null;
            _anchorStateRoot = null;
            _state = State.CheckedOut;
            tree = null!;
            return false;
        }
    }

    /// <summary>
    /// Stores a committed tree for the next matching flat-state scope.
    /// </summary>
    public void StoreAnchored(StateTree tree, Rebinder? rebind, Hash256 stateRoot)
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut)
            {
                throw new InvalidOperationException($"Expected checked-out Patricia trie, got {_state}.");
            }

            _tree = tree;
            if (rebind is not null) _rebind = rebind;
            if (_rebind is null)
            {
                throw new InvalidOperationException("A fresh preserved Patricia trie requires a rebinder.");
            }

            _anchorStateRoot = stateRoot;
            _state = State.Anchored;
        }
    }

    /// <summary>
    /// Clears the checked-out tree when it cannot be anchored.
    /// </summary>
    public void StoreCleared()
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut)
            {
                throw new InvalidOperationException($"Expected checked-out Patricia trie, got {_state}.");
            }

            _tree = null;
            _rebind = null;
            _anchorStateRoot = null;
            _state = State.Cleared;
        }
    }

    /// <summary>
    /// Best-effort clear used when a checked-out scope exits without anchoring the tree.
    /// </summary>
    public bool TryStoreCleared()
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut) return false;

            _tree = null;
            _rebind = null;
            _anchorStateRoot = null;
            _state = State.Cleared;
            return true;
        }
    }
}
