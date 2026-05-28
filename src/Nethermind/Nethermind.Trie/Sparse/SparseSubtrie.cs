// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// An independent arena holding sparse trie nodes. Implements reveal, update, remove, collapse,
/// and incremental RLP hashing.
/// </summary>
public sealed class SparseSubtrie : IDisposable
{
    private const int InitialArenaCapacity = 64;
    private const int InitialChildrenCapacity = 128;
    private const int InitialValuesCapacity = 32;

    private SparseTrieNode[] _arena;
    private SparseChildEntry[] _children;
    private byte[]?[] _values;
    private int _arenaCount;
    private int _childrenCount;
    private int _valuesCount;
    private int _freeHead = -1;

    public int Root { get; set; } = -1;
    public int NumLeaves { get; internal set; }
    public int NumDirtyLeaves { get; internal set; }

    public SparseSubtrie()
    {
        _arena = new SparseTrieNode[InitialArenaCapacity];
        _children = new SparseChildEntry[InitialChildrenCapacity];
        _values = new byte[]?[InitialValuesCapacity];
    }

    public bool IsEmpty => Root == -1 || _arena[Root].IsEmpty();


    public int AllocNode(SparseTrieNode node)
    {
        int idx;
        if (_freeHead >= 0)
        {
            idx = _freeHead;
            _freeHead = _arena[idx].ValueIndex;
            _arena[idx] = node;
        }
        else
        {
            idx = _arenaCount++;
            if (idx >= _arena.Length)
                Array.Resize(ref _arena, _arena.Length * 2);
            _arena[idx] = node;
        }
        return idx;
    }

    public void FreeNode(int idx)
    {
        SparseTrieNode node = _arena[idx];
        if (node.IsLeaf())
        {
            NumLeaves--;
            if (node.IsDirty()) NumDirtyLeaves--;
            if (node.ValueIndex >= 0) _values[node.ValueIndex] = null;
        }
        _arena[idx] = default;
        _arena[idx].ValueIndex = _freeHead;
        _freeHead = idx;
    }

    public int AllocChildren(int count)
    {
        int start = _childrenCount;
        _childrenCount += count;
        if (_childrenCount > _children.Length)
        {
            int newLen = _children.Length;
            while (newLen < _childrenCount) newLen *= 2;
            Array.Resize(ref _children, newLen);
        }
        return start;
    }

    public int AllocValue(byte[] value)
    {
        int idx = _valuesCount++;
        if (idx >= _values.Length)
            Array.Resize(ref _values, _values.Length * 2);
        _values[idx] = value;
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SparseTrieNode NodeAt(int idx) => ref _arena[idx];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SparseChildEntry ChildAt(int idx) => ref _children[idx];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[]? ValueAt(int idx) => idx >= 0 && idx < _values.Length ? _values[idx] : null;



    public int InsertLeaf(byte[] nibbleKey, byte[] value)
    {
        int valIdx = AllocValue(value);
        SparseTrieNode leaf = SparseTrieNode.CreateLeaf(nibbleKey, valIdx);
        int nodeIdx = AllocNode(leaf);
        NumLeaves++;
        NumDirtyLeaves++;
        return nodeIdx;
    }

    public void UpdateLeafValue(int nodeIdx, byte[] newValue)
    {
        ref SparseTrieNode node = ref _arena[nodeIdx];
        if (node.ValueIndex >= 0)
            _values[node.ValueIndex] = newValue;
        else
            node.ValueIndex = AllocValue(newValue);
        if (!node.IsDirty()) NumDirtyLeaves++;
        node.MarkDirty();
    }



    public int CreateBranchWithTwoChildren(
        byte[]? extensionKey, int nibbleA, int childA, int nibbleB, int childB)
    {
        TrieMask mask = TrieMask.Empty.SetBit(nibbleA).SetBit(nibbleB);
        int childStart = AllocChildren(2);
        int denseA = mask.DenseIndex(nibbleA);
        int denseB = mask.DenseIndex(nibbleB);
        _children[childStart + denseA] = SparseChildEntry.Revealed(childA);
        _children[childStart + denseB] = SparseChildEntry.Revealed(childB);

        SparseTrieNode branch = extensionKey is { Length: > 0 }
            ? SparseTrieNode.CreateBranchWithExtension(extensionKey, mask, childStart)
            : SparseTrieNode.CreateBranch(mask, childStart);
        return AllocNode(branch);
    }

    public void AddChildToBranch(int branchIdx, int nibble, int childIdx)
    {
        ref SparseTrieNode branch = ref _arena[branchIdx];
        TrieMask oldMask = branch.StateMask;
        TrieMask newMask = oldMask.SetBit(nibble);
        int oldCount = oldMask.CountBits();
        int newCount = oldCount + 1;
        int newStart = AllocChildren(newCount);
        int denseInsert = newMask.DenseIndex(nibble);

        int srcIdx = branch.ChildrenStart;
        int dstIdx = newStart;
        for (int d = 0; d < newCount; d++)
        {
            if (d == denseInsert)
                _children[dstIdx++] = SparseChildEntry.Revealed(childIdx);
            else
                _children[dstIdx++] = _children[srcIdx++];
        }
        branch.StateMask = newMask;
        branch.ChildrenStart = newStart;
        branch.MarkDirty();
    }

    public void RemoveChildFromBranch(int branchIdx, int nibble)
    {
        ref SparseTrieNode branch = ref _arena[branchIdx];
        TrieMask oldMask = branch.StateMask;
        int oldCount = oldMask.CountBits();
        int denseRemove = oldMask.DenseIndex(nibble);
        TrieMask newMask = oldMask.ClearBit(nibble);
        branch.BlindedMask = branch.BlindedMask.ClearBit(nibble);
        int newCount = oldCount - 1;

        if (newCount == 0)
        {
            branch.StateMask = TrieMask.Empty;
            branch.MarkDirty();
            return;
        }

        int newStart = AllocChildren(newCount);
        int srcIdx = branch.ChildrenStart;
        int dstIdx = newStart;
        for (int d = 0; d < oldCount; d++)
        {
            if (d != denseRemove)
                _children[dstIdx++] = _children[srcIdx++];
            else
                srcIdx++;
        }
        branch.StateMask = newMask;
        branch.ChildrenStart = newStart;
        branch.MarkDirty();
    }



    public int SplitAndInsertLeaf(int existingNodeIdx, byte[] existingKey,
        int commonPrefixLength, byte[] newLeafKey, byte[] newLeafValue)
    {
        int existingNibble = existingKey[commonPrefixLength];
        int newNibble = newLeafKey[commonPrefixLength];

        ref SparseTrieNode existing = ref _arena[existingNodeIdx];
        byte[] truncatedKey = existingKey[(commonPrefixLength + 1)..];
        if (existing.IsLeaf())
        {
            existing.ShortKey = truncatedKey;
            existing.MarkDirty();
        }
        else if (existing.IsBranch())
        {
            existing.ShortKey = truncatedKey.Length > 0 ? truncatedKey : null;
            existing.MarkDirty();
        }

        byte[] newKey = newLeafKey[(commonPrefixLength + 1)..];
        int newLeafIdx = InsertLeaf(newKey, newLeafValue);

        byte[]? extensionKey = commonPrefixLength > 0 ? existingKey[..commonPrefixLength] : null;
        return CreateBranchWithTwoChildren(extensionKey, existingNibble, existingNodeIdx, newNibble, newLeafIdx);
    }

    /// <summary>
    /// Collapses a branch with 0 or 1 children. Returns the replacement node index.
    /// Returns -1 if the remaining child is blinded (needs proof).
    /// </summary>
    public int CollapseBranch(int branchIdx)
    {
        int childCount = _arena[branchIdx].ChildCount();
        if (childCount == 0)
        {
            FreeNode(branchIdx);
            return AllocNode(SparseTrieNode.CreateEmpty());
        }
        if (childCount > 1) return branchIdx;

        int remainingNibble = -1;
        for (int n = 0; n < 16; n++)
        {
            if (_arena[branchIdx].StateMask.IsBitSet(n)) { remainingNibble = n; break; }
        }

        SparseChildEntry remainingEntry = _children[_arena[branchIdx].ChildrenStart];
        if (remainingEntry.IsBlinded) return -1;

        int childIdx = remainingEntry.ArenaIndex;

        // Build prefix: branch's extension key + remaining nibble
        byte[] prefix;
        byte[]? branchShortKey = _arena[branchIdx].ShortKey;
        if (branchShortKey is { Length: > 0 })
        {
            prefix = new byte[branchShortKey.Length + 1];
            branchShortKey.CopyTo(prefix.AsSpan());
            prefix[^1] = (byte)remainingNibble;
        }
        else
        {
            prefix = [(byte)remainingNibble];
        }

        if (_arena[childIdx].IsLeaf())
        {
            byte[] childKey = _arena[childIdx].ShortKey ?? [];
            byte[] newKey = new byte[prefix.Length + childKey.Length];
            prefix.CopyTo(newKey.AsSpan());
            childKey.CopyTo(newKey.AsSpan(prefix.Length));
            _arena[childIdx].ShortKey = newKey;
            _arena[childIdx].MarkDirty();
            FreeNode(branchIdx);
            return childIdx;
        }

        if (_arena[childIdx].IsBranch())
        {
            byte[] childShortKey = _arena[childIdx].ShortKey ?? [];
            byte[] newShortKey = new byte[prefix.Length + childShortKey.Length];
            prefix.CopyTo(newShortKey.AsSpan());
            childShortKey.CopyTo(newShortKey.AsSpan(prefix.Length));
            _arena[childIdx].ShortKey = newShortKey;
            _arena[childIdx].MarkDirty();
            FreeNode(branchIdx);
            return childIdx;
        }

        return -1;
    }



    public const int DeletedSentinel = -3;

    public enum UpdateResult { Applied, NeedsProof, NoChange }

    public UpdateResult UpdateSingleLeaf(ReadOnlySpan<byte> nibblePath, LeafUpdate update, out TreePath proofTarget)
    {
        proofTarget = default;
        if (Root == -1 || _arena[Root].IsEmpty())
        {
            if (update.IsDelete || update.Kind == LeafUpdateKind.Touched)
                return UpdateResult.NoChange;
            Root = InsertLeaf(nibblePath.ToArray(), update.Value!);
            return UpdateResult.Applied;
        }

        int oldRoot = Root;
        (UpdateResult result, int newRoot) = UpdateNode(Root, nibblePath, update, out proofTarget);
        if (result == UpdateResult.Applied)
        {
            if (newRoot == DeletedSentinel)
            {
                // Root leaf being deleted â€” UpdateAtLeaf no longer frees, so we must
                // (the leaf can't have a blinded sibling at this level, so no rollback needed).
                FreeNode(oldRoot);
                Root = AllocNode(SparseTrieNode.CreateEmpty());
            }
            else if (newRoot != Root)
                Root = newRoot;
        }
        return result;
    }

    /// <summary>
    /// Recursive update. Returns (result, replacementNodeIdx) where replacementNodeIdx
    /// is the node index that should replace nodeIdx in the caller's child array.
    /// Usually replacementNodeIdx == nodeIdx unless a structural change occurred.
    /// </summary>
    private (UpdateResult, int) UpdateNode(int nodeIdx, ReadOnlySpan<byte> path,
        LeafUpdate update, out TreePath proofTarget)
    {
        proofTarget = default;

        if (_arena[nodeIdx].IsBlinded())
        {
            proofTarget = TreePath.FromNibble(path);
            return (UpdateResult.NeedsProof, nodeIdx);
        }

        if (_arena[nodeIdx].IsLeaf())
            return UpdateAtLeaf(nodeIdx, path, update, out proofTarget);

        if (_arena[nodeIdx].IsBranch())
            return UpdateAtBranch(nodeIdx, path, update, out proofTarget);

        return (UpdateResult.NoChange, nodeIdx);
    }

    private (UpdateResult, int) UpdateAtLeaf(int nodeIdx, ReadOnlySpan<byte> path,
        LeafUpdate update, out TreePath proofTarget)
    {
        proofTarget = default;
        byte[] nodeKey = _arena[nodeIdx].ShortKey ?? [];
        int commonLen = CommonPrefixLength(path, nodeKey);

        if (commonLen == nodeKey.Length && commonLen == path.Length)
        {
            // Exact match
            if (update.IsDelete)
            {
                // Don't free yet. The caller (UpdateAtBranch) must run CollapseBranch first;
                // if collapse fails because a sibling is blinded, the leaf has to remain
                // available so the next retry — after the sibling proof is fetched — can
                // re-execute the deletion. Freeing here leaves the branch in a partial state
                // (StateMask cleared but no collapse) and the next UpdateLeaves call sees
                // "leaf already gone" and returns NoChange without invoking the proof
                // callback, so the retry loop exits and the wrong root gets computed.
                // The matching FreeNode lives in UpdateAtBranch where DeletedSentinel is consumed.
                return (UpdateResult.Applied, DeletedSentinel);
            }
            if (update.Kind == LeafUpdateKind.Touched) return (UpdateResult.NoChange, nodeIdx);

            byte[]? oldValue = ValueAt(_arena[nodeIdx].ValueIndex);
            if (oldValue is not null && update.Value is not null && oldValue.AsSpan().SequenceEqual(update.Value))
                return (UpdateResult.NoChange, nodeIdx);

            UpdateLeafValue(nodeIdx, update.Value!);
            return (UpdateResult.Applied, nodeIdx);
        }

        if (update.IsDelete || update.Kind == LeafUpdateKind.Touched)
            return (UpdateResult.NoChange, nodeIdx);

        // Diverged â€” split
        int newBranch = SplitAndInsertLeaf(nodeIdx, nodeKey, commonLen, path.ToArray(), update.Value!);
        return (UpdateResult.Applied, newBranch);
    }

    private (UpdateResult, int) UpdateAtBranch(int nodeIdx, ReadOnlySpan<byte> path,
        LeafUpdate update, out TreePath proofTarget)
    {
        proofTarget = default;
        byte[] shortKey = _arena[nodeIdx].ShortKey ?? [];

        // Extension-only state: BranchWithExtension was revealed from an Extension proof
        // but the underlying Branch's children were never revealed (stateMask == 0 with shortKey).
        // We cannot split or insert through this â€” the underlying Branch's structure is unknown.
        // Request a proof so the inner Branch gets revealed via MergeChildIntoBranchWithExtension.
        bool isExtensionOnly = shortKey.Length > 0 && _arena[nodeIdx].StateMask == TrieMask.Empty;
        if (isExtensionOnly && !(update.IsDelete || update.Kind == LeafUpdateKind.Touched))
        {
            proofTarget = TreePath.FromNibble(path);
            return (UpdateResult.NeedsProof, nodeIdx);
        }

        if (shortKey.Length > 0)
        {
            int commonLen = CommonPrefixLength(path, shortKey);
            if (commonLen < shortKey.Length)
            {
                if (update.IsDelete || update.Kind == LeafUpdateKind.Touched)
                    return (UpdateResult.NoChange, nodeIdx);

                int newBranch = SplitExtensionAndInsert(nodeIdx, shortKey, commonLen, path.ToArray(), update.Value!);
                return (UpdateResult.Applied, newBranch);
            }
            path = path[shortKey.Length..];
        }

        if (path.IsEmpty) return (UpdateResult.NoChange, nodeIdx);

        int nibble = path[0];
        ReadOnlySpan<byte> childPath = path[1..];

        if (!_arena[nodeIdx].StateMask.IsBitSet(nibble))
        {
            if (update.IsDelete || update.Kind == LeafUpdateKind.Touched)
                return (UpdateResult.NoChange, nodeIdx);
            int leafIdx = InsertLeaf(childPath.ToArray(), update.Value!);
            AddChildToBranch(nodeIdx, nibble, leafIdx);
            return (UpdateResult.Applied, nodeIdx);
        }

        int childDenseIdx = _arena[nodeIdx].DenseChildIndex(nibble);
        SparseChildEntry childEntry = _children[childDenseIdx];

        if (childEntry.IsBlinded)
        {
            proofTarget = TreePath.FromNibble(path);
            return (UpdateResult.NeedsProof, nodeIdx);
        }

        // Check for blinded sibling before deletion
        if (update.IsDelete && _arena[nodeIdx].ChildCount() == 2)
        {
            for (int n = 0; n < 16; n++)
            {
                if (n == nibble || !_arena[nodeIdx].StateMask.IsBitSet(n)) continue;
                int sibDense = _arena[nodeIdx].DenseChildIndex(n);
                if (_children[sibDense].IsBlinded)
                {
                    proofTarget = TreePath.FromNibble(path);
                    return (UpdateResult.NeedsProof, nodeIdx);
                }
                break;
            }
        }

        // Recurse into child
        int childNodeIdx = childEntry.ArenaIndex;
        (UpdateResult result, int childReplacement) = UpdateNode(childNodeIdx, childPath, update, out proofTarget);

        if (result != UpdateResult.Applied) return (result, nodeIdx);

        // Child changed â€” update our reference and handle structural changes
        if (childReplacement != childNodeIdx)
        {
            if (childReplacement == DeletedSentinel)
            {
                // UpdateAtLeaf no longer frees the leaf; we own the free here so we can
                // restore the branch atomically if CollapseBranch needs a proof.
                int leafIdxToFree = childNodeIdx;
                RemoveChildFromBranch(nodeIdx, nibble);
                int remainingCount = _arena[nodeIdx].ChildCount();
                if (remainingCount <= 1)
                {
                    int collapsed = CollapseBranch(nodeIdx);
                    if (collapsed == -1)
                    {
                        // Sibling is blinded â€” roll back: re-attach the leaf so the retry
                        // loop can re-execute the deletion after fetching the sibling proof.
                        // Without this, the next UpdateLeaves walks past a missing-child slot
                        // and silently treats the delete as a no-op, leaving a partial branch
                        // with one child + one blinded sibling that encodes to the wrong RLP.
                        AddChildToBranch(nodeIdx, nibble, leafIdxToFree);
                        return (UpdateResult.NeedsProof, nodeIdx);
                    }
                    FreeNode(leafIdxToFree);
                    return (UpdateResult.Applied, collapsed);
                }
                // Branch still has 2+ children after removal
                FreeNode(leafIdxToFree);
                _arena[nodeIdx].MarkDirty();
                return (UpdateResult.Applied, nodeIdx);
            }

            // Non-empty structural replacement (e.g., split created a new branch)
            int newDenseIdx = _arena[nodeIdx].DenseChildIndex(nibble);
            _children[newDenseIdx] = SparseChildEntry.Revealed(childReplacement);
        }

        _arena[nodeIdx].MarkDirty();
        return (UpdateResult.Applied, nodeIdx);
    }

    private int SplitExtensionAndInsert(int existingBranchIdx, byte[] extensionKey,
        int commonPrefixLength, byte[] newLeafPath, byte[] newLeafValue)
    {
        int existingNibble = extensionKey[commonPrefixLength];
        int newNibble = newLeafPath[commonPrefixLength];

        byte[] remainingExtKey = extensionKey[(commonPrefixLength + 1)..];
        _arena[existingBranchIdx].ShortKey = remainingExtKey.Length > 0 ? remainingExtKey : null;
        _arena[existingBranchIdx].MarkDirty();

        byte[] newKey = newLeafPath[(commonPrefixLength + 1)..];
        int newLeafIdx = InsertLeaf(newKey, newLeafValue);

        byte[]? wrapExtKey = commonPrefixLength > 0 ? extensionKey[..commonPrefixLength] : null;
        return CreateBranchWithTwoChildren(wrapExtKey, existingNibble, existingBranchIdx, newNibble, newLeafIdx);
    }



    public RlpNode UpdateCachedRlp()
    {
        if (Root == -1) return RlpNode.FromRlp([0x80]);
        if (_arena[Root].IsEmpty()) return RlpNode.FromRlp([0x80]);
        HashNode(Root);
        return _arena[Root].CachedRlp;
    }

    private void HashNode(int nodeIdx)
    {
        if (_arena[nodeIdx].IsCached() || _arena[nodeIdx].IsBlinded()) return;

        if (_arena[nodeIdx].IsLeaf())
        {
            EncodeLeaf(nodeIdx);
            return;
        }

        if (_arena[nodeIdx].IsBranch())
        {
            // Extension-only node: HasShortKey but empty StateMask. The single blinded child
            // reference sits at _children[ChildrenStart]. Patricia encodes this as a 2-item
            // extension (NOT extension wrapping a 17-item empty branch), so we must too.
            if (_arena[nodeIdx].HasShortKey() && _arena[nodeIdx].StateMask == TrieMask.Empty)
            {
                EncodeExtensionOnly(nodeIdx);
                return;
            }

            TrieMask mask = _arena[nodeIdx].StateMask;
            int childrenStart = _arena[nodeIdx].ChildrenStart;
            for (int n = 0; n < 16; n++)
            {
                if (!mask.IsBitSet(n)) continue;
                int denseIdx = childrenStart + mask.DenseIndex(n);
                SparseChildEntry entry = _children[denseIdx];
                // Dirty-path-only optimization: only recurse into children that need re-encoding.
                // Cached children already have valid CachedRlp; EncodeBranch will read it directly.
                // This is what makes cross-block trie reuse a net win (no O(arena size) walking).
                if (entry.IsRevealed && _arena[entry.ArenaIndex].IsDirty())
                    HashNode(entry.ArenaIndex);
            }
            EncodeBranch(nodeIdx);
            if (_arena[nodeIdx].HasShortKey()) WrapBranchWithExtension(nodeIdx);
        }
    }

    private void EncodeExtensionOnly(int nodeIdx)
    {
        byte[] key = _arena[nodeIdx].ShortKey ?? [];
        int childrenStart = _arena[nodeIdx].ChildrenStart;
        SparseChildEntry entry = _children[childrenStart];
        RlpNode childRlp = entry.IsRevealed ? _arena[entry.ArenaIndex].CachedRlp : entry.BlindedRlp;

        int hexLen = HexPrefix.ByteLength(key);
        Span<byte> keyBytes = hexLen <= 128 ? stackalloc byte[hexLen] : new byte[hexLen];
        HexPrefix.CopyToSpan(key, false, keyBytes);

        int keyRlpLen = Rlp.LengthOf(keyBytes);
        int childRefLen = childRlp.ChildRefLength;
        int contentLen = keyRlpLen + childRefLen;
        int totalLen = Rlp.LengthOfSequence(contentLen);

        byte[] rlp = new byte[totalLen];
        int pos = Rlp.StartSequence(rlp, 0, contentLen);
        pos = Rlp.Encode(rlp, pos, keyBytes);
        childRlp.WriteChildRef(rlp.AsSpan(pos));

        _arena[nodeIdx].FullRlp = rlp;
        _arena[nodeIdx].CachedRlp = rlp.Length >= 32
            ? RlpNode.FromRlpHashed(rlp)
            : RlpNode.FromRlp(rlp);
        _arena[nodeIdx].State = SparseNodeState.Cached;
    }

    private void EncodeLeaf(int nodeIdx)
    {
        byte[] key = _arena[nodeIdx].ShortKey ?? [];
        byte[]? value = ValueAt(_arena[nodeIdx].ValueIndex);
        value ??= [];

        int hexLen = HexPrefix.ByteLength(key);
        Span<byte> keyBytes = hexLen <= 128 ? stackalloc byte[hexLen] : new byte[hexLen];
        HexPrefix.CopyToSpan(key, true, keyBytes);

        int keyRlpLen = Rlp.LengthOf(keyBytes);
        int valRlpLen = Rlp.LengthOf(value);
        int contentLen = keyRlpLen + valRlpLen;
        int totalLen = Rlp.LengthOfSequence(contentLen);

        byte[] rlp = new byte[totalLen];
        int pos = Rlp.StartSequence(rlp, 0, contentLen);
        pos = Rlp.Encode(rlp, pos, keyBytes);
        Rlp.Encode(rlp, pos, value);

        _arena[nodeIdx].FullRlp = rlp;
        // CachedRlp is the child-ref form: hash for RLP >= 32 bytes, inline RLP otherwise.
        // Storing the hash here avoids re-keccaking when the parent encodes this child
        // (WriteChildRef on hash form just copies 32 bytes instead of computing keccak).
        _arena[nodeIdx].CachedRlp = rlp.Length >= 32
            ? RlpNode.FromRlpHashed(rlp)
            : RlpNode.FromRlp(rlp);
        _arena[nodeIdx].State = SparseNodeState.Cached;
    }

    private void EncodeBranch(int nodeIdx)
    {
        TrieMask mask = _arena[nodeIdx].StateMask;
        int childrenStart = _arena[nodeIdx].ChildrenStart;
        RlpNode[] childRlpArr = new RlpNode[16];

        int contentLen = 1; // trailing 0x80
        for (int n = 0; n < 16; n++)
        {
            if (!mask.IsBitSet(n)) { contentLen += 1; continue; }
            int denseIdx = childrenStart + mask.DenseIndex(n);
            SparseChildEntry entry = _children[denseIdx];
            RlpNode childRlp = entry.IsRevealed ? _arena[entry.ArenaIndex].CachedRlp : entry.BlindedRlp;
            childRlpArr[n] = childRlp;
            contentLen += childRlp.ChildRefLength;
        }

        int totalLen = Rlp.LengthOfSequence(contentLen);
        byte[] rlp = new byte[totalLen];
        int pos = Rlp.StartSequence(rlp, 0, contentLen);

        for (int n = 0; n < 16; n++)
        {
            if (!mask.IsBitSet(n)) { rlp[pos++] = 0x80; continue; }
            pos += childRlpArr[n].WriteChildRef(rlp.AsSpan(pos));
        }
        rlp[pos] = 0x80;

        _arena[nodeIdx].FullRlp = rlp;
        // CachedRlp is the child-ref form (see EncodeLeaf comment).
        _arena[nodeIdx].CachedRlp = rlp.Length >= 32
            ? RlpNode.FromRlpHashed(rlp)
            : RlpNode.FromRlp(rlp);
        _arena[nodeIdx].State = SparseNodeState.Cached;
    }

    private void WrapBranchWithExtension(int nodeIdx)
    {
        // Save the inner branch RLP before wrapping with extension â€” both may need DB entries
        _arena[nodeIdx].InnerBranchRlp = _arena[nodeIdx].FullRlp;

        byte[] extKey = _arena[nodeIdx].ShortKey!;
        RlpNode branchRlp = _arena[nodeIdx].CachedRlp;

        int hexLen = HexPrefix.ByteLength(extKey);
        Span<byte> keyBytes = hexLen <= 128 ? stackalloc byte[hexLen] : new byte[hexLen];
        HexPrefix.CopyToSpan(extKey, false, keyBytes);

        int keyRlpLen = Rlp.LengthOf(keyBytes);
        int childRefLen = branchRlp.ChildRefLength;
        int contentLen = keyRlpLen + childRefLen;
        int totalLen = Rlp.LengthOfSequence(contentLen);

        byte[] rlp = new byte[totalLen];
        int pos = Rlp.StartSequence(rlp, 0, contentLen);
        pos = Rlp.Encode(rlp, pos, keyBytes);
        branchRlp.WriteChildRef(rlp.AsSpan(pos));

        _arena[nodeIdx].FullRlp = rlp; // extension wrapper is now the "full" RLP
        _arena[nodeIdx].CachedRlp = rlp.Length >= 32
            ? RlpNode.FromRlpHashed(rlp)
            : RlpNode.FromRlp(rlp);
    }



    public void Wipe()
    {
        _arenaCount = 0;
        _childrenCount = 0;
        _valuesCount = 0;
        _freeHead = -1;
        NumLeaves = 0;
        NumDirtyLeaves = 0;
        Array.Clear(_arena);
        Array.Clear(_children);
        Array.Clear(_values);
        Root = AllocNode(SparseTrieNode.CreateEmpty());
    }


    /// <summary>
    /// Walks the trie along the target's nibble path. If the target's path reaches a Branch
    /// with exactly 2 children where one is the target's path and the OTHER is blinded,
    /// returns the blinded sibling's path and RlpNode (hash or inline). This is the structure
    /// needed when a deletion would collapse the parent and we need the sibling's content.
    /// Returns false if no such blinded sibling exists or path can't be walked.
    /// </summary>
    /// <summary>
    /// Walks the subtrie along <paramref name="targetNibbles"/> until it hits a blinded boundary
    /// (either a blinded branch child on the target's nibble or an extension-only branch whose
    /// inner branch hasn't been revealed). Returns the blinded RLP and where it sits so the proof
    /// reader can start from there instead of re-walking from root.
    /// </summary>
    /// <param name="targetNibbles">The full hashed key as nibbles.</param>
    /// <param name="blindedPath">Path from root to the blinded node.</param>
    /// <param name="blindedRlp">The blinded child's stored RLP (32-byte hash or inline).</param>
    /// <param name="remainingNibbleLen">How many nibbles of <paramref name="targetNibbles"/> remain past the blinded boundary.</param>
    /// <returns>True if a blinded boundary was found on the target's path; false if the path is fully revealed (no proof needed) or diverges.</returns>
    public bool TryFindBlindedEntryOnPath(
        ReadOnlySpan<byte> targetNibbles,
        out TreePath blindedPath,
        out RlpNode blindedRlp,
        out int remainingNibbleLen)
    {
        blindedPath = default;
        blindedRlp = default;
        remainingNibbleLen = 0;
        if (Root < 0) return false;

        TreePath currentPath = TreePath.Empty;
        int nodeIdx = Root;
        int nibblePos = 0;
        int safety = 0;
        while (safety++ < 128 && nibblePos < targetNibbles.Length)
        {
            ref SparseTrieNode node = ref _arena[nodeIdx];
            if (!node.IsBranch())
            {
                // Hit a leaf — no blinded boundary on this path.
                return false;
            }

            byte[] shortKey = node.ShortKey ?? [];

            // Extension-only state: BranchWithExtension whose inner branch is unrevealed
            // (StateMask is empty). The wrapper RLP encodes [extKey, branchRef], so we report
            // the boundary at the position BEFORE the shortKey is consumed and pass the
            // wrapper RLP. The proof reader's walker then descends through the extension key
            // normally, matching shortKey against the target nibbles and resolving the inner
            // branch via its embedded reference. Reporting at post-shortKey would mis-align
            // the walker: it would try to match shortKey against target nibbles that have
            // already moved past it, producing no descent and a useless wrapper-only proof.
            if (shortKey.Length > 0 && node.StateMask == TrieMask.Empty)
            {
                // Still verify the target matches the shortKey before declaring this the boundary.
                // If the target diverges within shortKey, no proof is needed (UpdateLeaves would
                // split the extension locally).
                int sharedLen = 0;
                int limit = Math.Min(shortKey.Length, targetNibbles.Length - nibblePos);
                while (sharedLen < limit && targetNibbles[nibblePos + sharedLen] == shortKey[sharedLen]) sharedLen++;
                if (sharedLen < shortKey.Length) return false;

                blindedPath = currentPath; // pre-shortKey position
                blindedRlp = node.CachedRlp;
                remainingNibbleLen = targetNibbles.Length - nibblePos;
                return true;
            }

            if (shortKey.Length > 0)
            {
                int sharedLen = 0;
                int limit = Math.Min(shortKey.Length, targetNibbles.Length - nibblePos);
                while (sharedLen < limit && targetNibbles[nibblePos + sharedLen] == shortKey[sharedLen]) sharedLen++;
                if (sharedLen < shortKey.Length) return false; // path diverges within shortKey
                currentPath = currentPath.Append(shortKey);
                nibblePos += shortKey.Length;
            }

            if (nibblePos >= targetNibbles.Length) return false;
            byte targetNibble = targetNibbles[nibblePos];
            if (!node.StateMask.IsBitSet(targetNibble)) return false; // empty slot, absence

            int denseIdx = node.DenseChildIndex(targetNibble);
            SparseChildEntry childEntry = _children[denseIdx];

            if (childEntry.IsBlinded)
            {
                blindedPath = currentPath.Append(targetNibble);
                blindedRlp = childEntry.BlindedRlp;
                remainingNibbleLen = targetNibbles.Length - nibblePos - 1;
                return true;
            }

            currentPath = currentPath.Append(targetNibble);
            nodeIdx = childEntry.ArenaIndex;
            nibblePos++;
        }
        return false;
    }

    public bool TryFindBlindedSiblingForDeletion(
        ReadOnlySpan<byte> targetNibbles,
        out TreePath siblingPath,
        out RlpNode siblingRlp)
    {
        siblingPath = default;
        siblingRlp = default;
        if (Root < 0) return false;

        TreePath currentPath = TreePath.Empty;
        int nodeIdx = Root;
        int nibblePos = 0;
        int safety = 0;
        while (safety++ < 100 && nibblePos < targetNibbles.Length)
        {
            ref SparseTrieNode node = ref _arena[nodeIdx];
            if (!node.IsBranch()) return false;

            byte[] shortKey = node.ShortKey ?? [];
            if (shortKey.Length > 0)
            {
                int sharedLen = 0;
                int limit = Math.Min(shortKey.Length, targetNibbles.Length - nibblePos);
                while (sharedLen < limit && targetNibbles[nibblePos + sharedLen] == shortKey[sharedLen]) sharedLen++;
                if (sharedLen < shortKey.Length) return false; // path diverges within shortKey
                currentPath = currentPath.Append(shortKey);
                nibblePos += shortKey.Length;
            }
            if (nibblePos >= targetNibbles.Length) return false;

            byte targetNibble = targetNibbles[nibblePos];
            if (!node.StateMask.IsBitSet(targetNibble)) return false;

            // If this branch has exactly 2 children and the OTHER is blinded, that's our sibling
            if (node.ChildCount() == 2)
            {
                for (int sibNibble = 0; sibNibble < 16; sibNibble++)
                {
                    if (sibNibble == targetNibble || !node.StateMask.IsBitSet(sibNibble)) continue;
                    int sibDense = node.DenseChildIndex(sibNibble);
                    SparseChildEntry sibEntry = _children[sibDense];
                    if (sibEntry.IsBlinded)
                    {
                        siblingPath = currentPath.Append((byte)sibNibble);
                        siblingRlp = sibEntry.BlindedRlp;
                        return true;
                    }
                }
            }

            // Descend further (only if target's child is revealed; if blinded, normal flow handles it)
            int denseIdx = node.DenseChildIndex(targetNibble);
            SparseChildEntry childEntry = _children[denseIdx];
            if (childEntry.IsBlinded) return false; // normal NeedsProof path handles this
            currentPath = currentPath.Append(targetNibble);
            nodeIdx = childEntry.ArenaIndex;
            nibblePos++;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
            if (a[i] != b[i]) return i;
        return minLen;
    }

    public void Dispose() { }
}
