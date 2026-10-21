// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <inheritdoc cref="TracksPath" path="/summary"/>
    /// <remarks>
    /// The guest's nodes come from the witness, keyed by hash alone (see <c>WitnessNodeStorage.zkevm.cs</c>), so
    /// the path would only label exceptions, at a nibble write and a nibble clear per level.
    /// </remarks>
    private static bool TracksPath => false;

    /// <summary>How <see cref="ShouldUpdateChild"/> is inlined: always.</summary>
    /// <remarks>Asked per level as a trie write climbs back to the root; out of line, the call costs more than its few compares.</remarks>
    private const MethodImplOptions ShouldUpdateChildInlining = MethodImplOptions.AggressiveInlining;

    /// <inheritdoc cref="ReleasesPoppedFrames" path="/summary"/>
    /// <remarks>Not in the guest: it verifies one block and exits, so what the stack keeps alive costs nothing, while
    /// the clear is a second index computation and three stores per level of every write's climb, and a memset for
    /// every write that ends its climb early.</remarks>
    private static bool ReleasesPoppedFrames => false;

    /// <summary>Whether a trie write that changes a level unseals every ancestor still on its stack at once.</summary>
    /// <remarks>
    /// Once a level takes a changed child, every level above it does too, and here <see cref="TrieNode.Unseal"/> leaves the
    /// node in place. Unsealing the rest of the stack in one loop leaves each ancestor pending with the child it already
    /// holds, so <see cref="IsUnchangedPendingLevel"/> ends the climb at the next level instead of each paying a full step.
    /// </remarks>
    private static bool UnsealsAncestorsAtOnce => true;

    /// <summary>Whether a trie write may stop climbing at this level, the rest of the climb changing nothing.</summary>
    /// <remarks>
    /// So it is when the parent is dirty and unhashed and already holds the same, unhashed child: updating it would store
    /// that child again and clear a hash already cleared. A write leaves each ancestor of a node it changes dirty and
    /// unhashed and holding that node, hashing runs bottom up, and a sealed node is never pending, so every level
    /// above is in the same state. Saves the write the climb through the levels an earlier write of the block has
    /// already dirtied, the top of a storage trie for all but its first slot.
    /// <para>
    /// Relies on the guest writing one key at a time through this walk (the state's <c>BulkWriteThreshold</c> keeps
    /// <c>BulkSet</c> off), and on no trie written after a root computation holding an embedded branch or
    /// extension, which keeps no hash once encoded while its ancestors gain one: under keccak keys it would need a shared
    /// prefix of about 54 nibbles, and the raw-keyed tries (transactions, receipts, withdrawals) are written in full
    /// before their root is computed.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnchangedPendingLevel(TrieNode parent, int childIndex, TrieNode? originalChild, TrieNode? child) =>
        // The parent's hash first: a node read from the witness has one, and it fails most levels on the first load.
        parent.Keccak is null && child is not null && ReferenceEquals(originalChild, child) && parent.IsPendingWith(childIndex, child);

    /// <remarks>
    /// Always, sparing the nibble expansion of the key: a branch takes its nibble from the key's bytes, and a leaf is
    /// matched against the key's trailing bytes in its hex-prefix form (see <see cref="LeafData.KeyIsSuffixOf"/>). Without
    /// the std form's exception enhancement, as the guest fails the block on any exception.
    /// </remarks>
    private partial bool TryGetByRawKey(ReadOnlySpan<byte> rawKey, Hash256? rootHash, out ReadOnlySpan<byte> value)
    {
        TrieNode? root = rootHash is null ? RootRef : TrieStore.FindCachedOrUnknown(TreePath.Empty, rootHash);
        CappedArray<byte> result = GetByRawKey(rawKey, root);
        value = result.IsNull ? ReadOnlySpan<byte>.Empty : result.AsSpan();
        return true;
    }

    /// <summary>Whether <paramref name="node"/> is a leaf at <paramref name="key"/>, a nibble path, told without
    /// expanding a key the leaf has kept in hex-prefix form.</summary>
    /// <remarks>Spares the expansion for a leaf a write only changes the value of, re-encoded from its stored key item.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLeafWithKey(TrieNode node, ReadOnlySpan<byte> key) => node.NodeData is LeafData leaf && leaf.KeyEquals(key);

    /// <summary>The value at <paramref name="key"/> below <paramref name="node"/>, as <see cref="GetNew"/> finds it from the key's nibbles.</summary>
    private CappedArray<byte> GetByRawKey(ReadOnlySpan<byte> key, TrieNode? node)
    {
        TreePath path = TreePath.Empty;
        int nibbleCount = 2 * key.Length;
        int consumed = 0;
        while (node is not null)
        {
            node.ResolveNode(TrieStore, path);
            INodeData? data = node.NodeData;
            if (data is BranchData)
            {
                // A value held by a branch is not supported, as in GetNew.
                if (consumed == nibbleCount) return default;
                node = node.GetChildWithChildPath(TrieStore, ref path, Nibbles.NibbleAt(key, consumed++));
            }
            else if (data is LeafData leaf)
            {
                return leaf.KeyIsSuffixOf(key, nibbleCount - consumed) ? leaf.Value : default;
            }
            else
            {
                byte[] extensionKey = node.Key!;
                if (extensionKey.Length > nibbleCount - consumed) return default;
                for (int i = 0; i < extensionKey.Length; i++)
                {
                    if (extensionKey[i] != Nibbles.NibbleAt(key, consumed + i)) return default;
                }

                consumed += extensionKey.Length;
                node = node.GetChildWithChildPath(TrieStore, ref path, 0);
            }
        }

        return default;
    }

    partial void AdjustBulkSetFlags(ref Flags flags) => flags |= Flags.DoNotParallelize;

    /// <remarks>Where <see cref="FirstTouchMemoryFlag"/> is on, a branch hashed as soon as it is encoded keeps its previous
    /// RLP (see <c>TrieNode.KeepsRlpOfHashedBranch</c>), so a commit persists a stale encoding. The guest never commits,
    /// and this makes one fail loudly. It is checked after the commit: checked before, it would leave the commit
    /// unreachable and change the guest's code generation.</remarks>
    partial void ThrowIfCommitUnsupported()
    {
        if (FirstTouchMemoryFlag.IsActive) ThrowReadOnlyTrieException();
    }
}
