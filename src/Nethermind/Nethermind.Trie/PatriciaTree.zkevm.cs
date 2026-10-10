// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

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
    /// the clear is a second index computation and three stores per level of every write's climb.</remarks>
    private static bool ReleasesPoppedFrames => false;

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

    partial void AdjustBulkSetFlags(ref Flags flags) => flags |= Flags.DoNotParallelize;
}
