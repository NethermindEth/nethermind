// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    // The state and storage tries key by keccak.
    private const int SecuredKeyNibbles = 2 * Hash256.Size;

    // The key length of the last write that deleted a leaf, which alone collapses a branch.
    private int _deletedKeyNibbles;

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

    /// <remarks>Always, sparing the nibble expansion of the key as <see cref="TryGetByRawKey"/> does; see <see cref="SetByRawKey"/>.</remarks>
    private partial bool TrySetByRawKey(ReadOnlySpan<byte> rawKey, CappedArray<byte> value)
    {
        TraverseStack traverseStack = GetTraverseStack();
        TreePath empty = TreePath.Empty;
        RootRef = SetByRawKey(traverseStack, rawKey, value, ref empty, RootRef);
        ReturnTraverseStack(traverseStack);
        return true;
    }

    /// <summary>Writes <paramref name="value"/> at <paramref name="key"/> below <paramref name="node"/>, leaving the trie
    /// <see cref="SetNew"/> leaves for the key's nibbles.</summary>
    /// <remarks>
    /// A branch takes its nibble from the key's bytes, and a leaf keeping its key in hex-prefix form is compared, split and
    /// created in that form, so neither the key nor the leaf's key is expanded. Other node keys are read as nibbles.
    /// It repeats the steps of <see cref="SetNew"/>, so a change to either belongs in both.
    /// </remarks>
    private TrieNode? SetByRawKey(TraverseStack traverseStack, ReadOnlySpan<byte> key, CappedArray<byte> value, ref TreePath path, TrieNode? node)
    {
        TrieNode? originalNode = node;
        int originalPathLength = path.Length;
        int nibbleCount = 2 * key.Length;
        int consumed = 0;

        while (true)
        {
            if (node is null)
            {
                node = value.IsNullOrEmpty ? null : new TrieNode(LeafData.WithKeyFrom(key, consumed, value));
                break;
            }

            node.ResolveNode(TrieStore, path);

            if (!node.IsBranch)
            {
                int remaining = nibbleCount - consumed;
                ReadOnlySpan<byte> compactKey = default;
                bool isCompactLeaf = node.NodeData is LeafData leaf && leaf.TryGetCompactKey(out compactKey);
                // A hex-prefix key's nibbles start after its flag nibble, and after the padding nibble of an even key.
                int compactStart = isCompactLeaf ? 2 - ((compactKey[0] >> 4) & 1) : 0;
                byte[]? nibbleKey = isCompactLeaf ? null : node.Key!;
                int keyLength = isCompactLeaf ? 2 * compactKey.Length - compactStart : nibbleKey!.Length;
                int commonPrefixLength = isCompactLeaf
                    ? Nibbles.CommonPrefixLength(compactKey, compactStart, key, consumed, Math.Min(keyLength, remaining))
                    : CommonPrefixLength(nibbleKey!, key, consumed, Math.Min(keyLength, remaining));
                if (nibbleCount == SecuredKeyNibbles && (node.IsExtension ? keyLength >= remaining : keyLength != remaining)) ThrowPathExceedsKey();

                if (commonPrefixLength == keyLength)
                {
                    if (node.IsExtension)
                    {
                        TrieNode? extensionChild = node.GetChildWithChildPath(TrieStore, ref path, 0);
                        traverseStack.Push(new TraverseStackFrame()
                        {
                            Node = node,
                            OriginalChild = extensionChild,
                            ChildIdx = 0,
                        });

                        consumed += keyLength;
                        node = extensionChild;
                        continue;
                    }

                    if (value.IsNullOrEmpty)
                    {
                        _deletedKeyNibbles = nibbleCount;
                        node = null;
                    }
                    else if (node.Value.AsSpan().SequenceEqual(value.AsSpan()))
                    {
                        path.TruncateMut(originalPathLength);
                        traverseStack.Clear();
                        return originalNode;
                    }
                    else
                    {
                        if (node.IsSealed) node = node.Unseal();
                        node.Value = value;
                        node.Keccak = null;
                    }

                    break;
                }

                if (value.IsNullOrEmpty)
                {
                    path.TruncateMut(originalPathLength);
                    traverseStack.Clear();
                    return originalNode;
                }

                TrieNode theBranch = TrieNodeFactory.CreateBranch();
                if (isCompactLeaf)
                {
                    int leafNibble = compactStart + commonPrefixLength;
                    theBranch[Nibbles.NibbleAt(compactKey, leafNibble)] = new TrieNode(LeafData.WithKeyFrom(compactKey, leafNibble + 1, node.Value));
                }
                else
                {
                    int currentNodeNib = nibbleKey![commonPrefixLength];
                    if (nibbleKey.Length == commonPrefixLength + 1 && node.IsExtension)
                    {
                        int originalLength = path.Length;
                        path.AppendMut(nibbleKey);
                        theBranch[currentNodeNib] = node.GetChildWithChildPath(TrieStore, ref path, 0);
                        path.TruncateMut(originalLength);
                    }
                    else
                    {
                        theBranch[currentNodeNib] = node.CloneWithChangedKey(HexPrefix.GetArray(nibbleKey.AsSpan(commonPrefixLength + 1)));
                    }
                }

                int keyNibble = consumed + commonPrefixLength;
                theBranch[Nibbles.NibbleAt(key, keyNibble)] = new TrieNode(LeafData.WithKeyFrom(key, keyNibble + 1, value));

                node = commonPrefixLength == 0 ? theBranch : TrieNodeFactory.CreateExtension(KeyNibbles(key, consumed, commonPrefixLength), theBranch);
                break;
            }

            if (consumed == nibbleCount) ThrowPathExceedsKey();
            int nib = Nibbles.NibbleAt(key, consumed);
            TrieNode? child = node.GetChildWithChildPath(TrieStore, ref path, nib);
            traverseStack.Push(new TraverseStackFrame()
            {
                Node = node,
                OriginalChild = child,
                ChildIdx = nib,
            });

            node = child;
            consumed++;
        }

        return ClimbToRoot(traverseStack, node, ref path, originalPathLength, originalNode);
    }

    /// <summary>Length of the common prefix, at most <paramref name="count"/>, of <paramref name="nibbles"/> and the nibbles
    /// of <paramref name="key"/> from nibble <paramref name="start"/>.</summary>
    private static int CommonPrefixLength(byte[] nibbles, ReadOnlySpan<byte> key, int start, int count)
    {
        int i = 0;
        while (i < count && nibbles[i] == Nibbles.NibbleAt(key, start + i)) i++;
        return i;
    }

    /// <summary>The <paramref name="count"/> nibbles of <paramref name="key"/> from nibble <paramref name="start"/>.</summary>
    [SkipLocalsInit]
    private static byte[] KeyNibbles(ReadOnlySpan<byte> key, int start, int count)
    {
        Span<byte> nibbles = count <= MaxKeyStackAlloc ? stackalloc byte[MaxKeyStackAlloc] : new byte[count];
        nibbles = nibbles[..count];
        for (int i = 0; i < nibbles.Length; i++) nibbles[i] = (byte)Nibbles.NibbleAt(key, start + i);
        return HexPrefix.GetArray(nibbles);
    }

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
                if (consumed == nibbleCount)
                {
                    if (nibbleCount == SecuredKeyNibbles) ThrowPathExceedsKey();
                    return default;
                }

                node = node.GetChildWithChildPath(TrieStore, ref path, Nibbles.NibbleAt(key, consumed++));
            }
            else if (data is LeafData leaf)
            {
                if (leaf.KeyIsSuffixOf(key, nibbleCount - consumed)) return leaf.Value;
                if (nibbleCount == SecuredKeyNibbles && leaf.KeyNibbleCount != nibbleCount - consumed) ThrowPathExceedsKey();
                return default;
            }
            else
            {
                byte[] extensionKey = node.Key!;
                if (extensionKey.Length >= nibbleCount - consumed)
                {
                    if (nibbleCount == SecuredKeyNibbles) ThrowPathExceedsKey();
                    if (extensionKey.Length > nibbleCount - consumed) return default;
                }
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

    /// <remarks>Only in a secured trie. The sibling the branch collapsed into sits one level
    /// below it, so its path is checked by checking the collapsed node's at the branch's depth.</remarks>
    partial void ValidateCollapsedBranch(TraverseStack ancestors, TrieNode? node)
    {
        if (node is null || _deletedKeyNibbles != SecuredKeyNibbles) return;

        int depth = 0;
        foreach (TraverseStackFrame frame in ancestors.Frames) depth += frame.Node.IsExtension ? frame.Node.Key!.Length : 1;
        int remaining = SecuredKeyNibbles - depth;
        if (node.IsBranch ? remaining <= 0 : node.IsLeaf ? node.Key!.Length != remaining : node.Key!.Length >= remaining) ThrowPathExceedsKey();
    }

    /// <summary>Rejects a secured trie node whose path does not end at the length of the hashed keys.</summary>
    [DoesNotReturn, StackTraceHidden]
    private static void ThrowPathExceedsKey() => throw new TrieException("A witness trie path does not end at the length of its hashed keys");

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
