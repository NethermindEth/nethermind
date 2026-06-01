// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

namespace Nethermind.State.Flat;

/// <summary>
/// Cross-block preservation of the warmed Patricia account <see cref="StateTree"/> for the
/// non-sparse flat path (gated by <c>FlatDbConfig.PreservePatriciaTrie</c>). Legacy-Patricia
/// analogue of <see cref="PreservedSparseTrie"/>: holds the resolved trie node graph across
/// consecutive blocks so the next block reuses the warm structure instead of re-resolving from
/// caches/disk.
/// </summary>
/// <remarks>
/// Ownership protocol mirrors <see cref="PreservedSparseTrie"/> (<c>Empty/Anchored/Cleared/CheckedOut</c>).
/// Unlike the sparse arena, a <see cref="StateTree"/> is bound to a per-block <see cref="SnapshotBundle"/>
/// via its trie-store adapter; that bundle is disposed at end of block. The store therefore also holds a
/// <see cref="Rebinder"/> delegate that repoints the tree's adapter at the new block's bundle on reuse
/// (the adapter type itself is internal, so it is captured behind this delegate rather than exposed).
/// A reused tree's <c>RootRef</c> already equals the anchor (= the new parent root), and the caller
/// recomputes the authoritative root via <c>UpdateRootHash</c>, so reuse cannot change consensus — a
/// parent-root mismatch falls back to a fresh tree.
/// </remarks>
public sealed class PreservedPatriciaTrie
{
    /// <summary>Repoints a reused tree's trie-store adapter at the current block's bundle/quota.</summary>
    public delegate void Rebinder(SnapshotBundle bundle, ConcurrencyController quota);

    private readonly object _lock = new();
    private StateTree? _tree;
    private Rebinder? _rebind;
    private Hash256? _anchorStateRoot;
    private State _state = State.Empty;

    private enum State { Empty, Anchored, Cleared, CheckedOut }

    /// <summary>
    /// Attempts to hand back the preserved tree for reuse when its anchor equals
    /// <paramref name="parentStateRoot"/>. On success the tree's adapter has been rebound to
    /// <paramref name="newBundle"/>/<paramref name="newQuota"/> via the retained rebinder and the tree
    /// is ready to use. On any other state (no anchor, or anchor mismatch) returns false and the caller
    /// must build a fresh tree (and supply its rebinder on the next <see cref="StoreAnchored"/>).
    /// </summary>
    public bool TryTake(
        Hash256 parentStateRoot,
        SnapshotBundle newBundle,
        ConcurrencyController newQuota,
        out StateTree tree)
    {
        lock (_lock)
        {
            if (_state == State.CheckedOut)
                throw new InvalidOperationException("Preserved Patricia trie already checked out by another block");

            if (_state == State.Anchored && _anchorStateRoot == parentStateRoot && _tree is not null && _rebind is not null)
            {
                _rebind(newBundle, newQuota);
                tree = _tree;
                _tree = null;
                _anchorStateRoot = null;
                _state = State.CheckedOut;
                return true;
            }

            // No reusable tree — drop any retained one and let the caller build fresh.
            _tree = null;
            _rebind = null;
            _anchorStateRoot = null;
            _state = State.CheckedOut;
            tree = null!;
            return false;
        }
    }

    /// <summary>
    /// Retains the committed tree for next-block reuse, keyed by its root. <paramref name="rebind"/> is
    /// supplied on the first anchor (fresh-tree path); on the reuse path pass null and the store keeps
    /// the rebinder it already holds.
    /// </summary>
    public void StoreAnchored(StateTree tree, Rebinder? rebind, Hash256 stateRoot)
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut)
                throw new InvalidOperationException($"Expected CheckedOut, got {_state}");
            _tree = tree;
            if (rebind is not null) _rebind = rebind;
            if (_rebind is null)
                throw new InvalidOperationException("StoreAnchored requires a rebinder on the first anchor.");
            _anchorStateRoot = stateRoot;
            _state = State.Anchored;
        }
    }

    /// <summary>Drops the tree (root didn't match / reorg). Next block starts cold.</summary>
    public void StoreCleared()
    {
        lock (_lock)
        {
            if (_state != State.CheckedOut)
                throw new InvalidOperationException($"Expected CheckedOut, got {_state}");
            _tree = null;
            _rebind = null;
            _anchorStateRoot = null;
            _state = State.Cleared;
        }
    }

    /// <summary>Best-effort clear used on scope disposal without commit (reorg/exception). No-op if
    /// the tree was already stored back.</summary>
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
