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

    #region Arena Management

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

    #endregion

    #region Leaf Operations

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

    #endregion

    #region Branch Operations

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

    #endregion

    #region Split and Collapse

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

    #endregion

    #region Update Leaves

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

        (UpdateResult result, int newRoot) = UpdateNode(Root, nibblePath, update, out proofTarget);
        if (result == UpdateResult.Applied)
        {
            if (newRoot == DeletedSentinel)
                Root = AllocNode(SparseTrieNode.CreateEmpty());
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
                FreeNode(nodeIdx);
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

        // Diverged — split
        int newBranch = SplitAndInsertLeaf(nodeIdx, nodeKey, commonLen, path.ToArray(), update.Value!);
        return (UpdateResult.Applied, newBranch);
    }

    private (UpdateResult, int) UpdateAtBranch(int nodeIdx, ReadOnlySpan<byte> path,
        LeafUpdate update, out TreePath proofTarget)
    {
        proofTarget = default;
        byte[] shortKey = _arena[nodeIdx].ShortKey ?? [];

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

        // Child changed — update our reference and handle structural changes
        if (childReplacement != childNodeIdx)
        {
            if (childReplacement == DeletedSentinel)
            {
                // Child was deleted — remove from branch
                RemoveChildFromBranch(nodeIdx, nibble);
                int remainingCount = _arena[nodeIdx].ChildCount();
                if (remainingCount <= 1)
                {
                    int collapsed = CollapseBranch(nodeIdx);
                    if (collapsed == -1) return (UpdateResult.NeedsProof, nodeIdx);
                    return (UpdateResult.Applied, collapsed);
                }
                // Branch still has 2+ children after removal
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

    #endregion

    #region Incremental Hashing

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
            TrieMask mask = _arena[nodeIdx].StateMask;
            int childrenStart = _arena[nodeIdx].ChildrenStart;
            for (int n = 0; n < 16; n++)
            {
                if (!mask.IsBitSet(n)) continue;
                int denseIdx = childrenStart + mask.DenseIndex(n);
                SparseChildEntry entry = _children[denseIdx];
                if (entry.IsRevealed) HashNode(entry.ArenaIndex);
            }
            EncodeBranch(nodeIdx);
            if (_arena[nodeIdx].HasShortKey()) WrapBranchWithExtension(nodeIdx);
        }
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

        _arena[nodeIdx].CachedRlp = RlpNode.FromRlp(rlp);
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

        _arena[nodeIdx].CachedRlp = RlpNode.FromRlp(rlp);
        _arena[nodeIdx].State = SparseNodeState.Cached;
    }

    private void WrapBranchWithExtension(int nodeIdx)
    {
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

        _arena[nodeIdx].CachedRlp = RlpNode.FromRlp(rlp);
    }

    #endregion

    #region Wipe

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

    #endregion

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
