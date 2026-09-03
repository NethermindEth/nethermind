// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// Backing store for the PBT tree: the stem trie node groups and the per-stem 256-leaf blobs. Reads
/// return a poolable, disposable wrapper (null = absent); writes hand over one (null removes the group
/// / deletes the stem).
/// </summary>
/// <remarks>
/// A write transfers the caller's lease on the value: the store owns it from that point and must
/// release it once done — by disposing it after copying, or, if it retains the memory, when it drops
/// the value. It never acquires a lease of its own to do so, and the caller must not use the memory
/// afterwards; a caller that needs to keep reading it acquires its own lease first.
/// <para>
/// An implementation must bear reads and writes from several threads at once: a fold runs across as
/// many threads as it is given, each reading and writing as it goes. What it needs of them is
/// structural safety and nothing more — two threads never touch the same node key or stem, since the
/// ranges they fold are disjoint subtrees, and a value is always read before it is written.
/// </para>
/// </remarks>
public interface IPbtStore
{
    /// <summary>Gets the trie node group at <paramref name="key"/> that represents <paramref name="hash"/>.</summary>
    RefCountingMemory? GetTrieNode(in TrieNodeKey key, in ValueHash256 hash);

    /// <summary>Stores <paramref name="node"/> at <paramref name="key"/> as the group that represents <paramref name="hash"/>.</summary>
    void SetTrieNode(in TrieNodeKey key, in ValueHash256 hash, RefCountingMemory? node);

    /// <summary>Gets the leaf blob for <paramref name="stem"/> whose leaf-subtree root is <paramref name="hash"/>.</summary>
    RefCountingMemory? GetLeafBlob(in Stem stem, in ValueHash256 hash);

    /// <summary>Stores <paramref name="blob"/> for <paramref name="stem"/> with leaf-subtree root <paramref name="hash"/>.</summary>
    void SetLeafBlob(in Stem stem, in ValueHash256 hash, RefCountingMemory? blob);
}
