// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Atomically applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static class TrieUpdater
{
    private static readonly PbtNodePath RootPath = new([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// The final complete-key set is validated before any node is changed. All leaf and node writes are
    /// staged and handed to <see cref="IPbtStore.Apply"/> only after the complete mutation succeeds.
    /// </remarks>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return currentRoot;

        SortedDictionary<PbtFullKey, PbtWriteOperation> operations = [];
        foreach (PbtWriteOperation operation in changes.Operations) operations[operation.Key] = operation;

        ValidateBatchKeys(operations);
        Overlay overlay = new(store);
        ValueHash256 root = currentRoot;
        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind != PbtWriteOperationKind.Delete || root == default) continue;
            root = Delete(overlay, RootPath, root, operation.Key, out bool removed);
            if (removed) overlay.SetLeaf(operation.Key, null);
        }

        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind != PbtWriteOperationKind.Set) continue;
            root = Insert(overlay, RootPath, root, operation.Key, operation.Value);
            overlay.SetLeaf(operation.Key, operation.Value);
        }

        store.Apply(root, overlay.LeafMutations, overlay.NodeMutations);
        return root;
    }

    private static ValueHash256 Insert(
        Overlay overlay, PbtNodePath path, ValueHash256 expectedHash, PbtFullKey key, in ValueHash256 value)
    {
        if (expectedHash == default)
        {
            PbtLeafNode leaf = new(key, value.Bytes.ToArray());
            overlay.Store(path, leaf);
            return leaf.Hash;
        }

        PbtNode current = overlay.Load(path, expectedHash);
        if (current is PbtLeafNode existingLeaf)
        {
            if (existingLeaf.Key.Equals(key))
            {
                PbtLeafNode replacement = new(key, value.Bytes.ToArray());
                overlay.Store(path, replacement);
                return replacement.Hash;
            }

            int differingBit = existingLeaf.Key.FirstDifferingBit(key, path.BitDepth);
            if (differingBit == Math.Min(existingLeaf.Key.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            PbtBitPrefix prefix = PbtBitPrefix.FromKey(key, path.BitDepth, differingBit - path.BitDepth);
            int existingDirection = existingLeaf.Key.GetBit(differingBit);
            PbtNodePath existingPath = PbtNodePath.FromKey(existingLeaf.Key, differingBit + 1);
            PbtNodePath newPath = PbtNodePath.FromKey(key, differingBit + 1);
            overlay.Remove(path);
            overlay.Store(existingPath, existingLeaf);
            PbtLeafNode newLeaf = new(key, value.Bytes.ToArray());
            overlay.Store(newPath, newLeaf);
            PbtBranchNode split = existingDirection == 0
                ? new(prefix, existingLeaf.Hash, newLeaf.Hash)
                : new(prefix, newLeaf.Hash, existingLeaf.Hash);
            overlay.Store(path, split);
            return split.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int available = key.BitLength - path.BitDepth;
        int matched = MatchingPrefixBits(branch.Prefix, key, path.BitDepth);
        if (matched == available)
            throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
        if (matched < branch.Prefix.BitCount)
        {
            PbtBitPrefix common = PbtBitPrefix.FromKey(key, path.BitDepth, matched);
            int existingDirection = branch.Prefix.GetBit(matched);
            PbtBranchNode relocated = new(Slice(branch.Prefix, matched + 1), branch.LeftHash, branch.RightHash);
            PbtNodePath existingPath = path.Append(common, existingDirection);
            int newDirection = key.GetBit(path.BitDepth + matched);
            PbtNodePath newPath = PbtNodePath.FromKey(key, path.BitDepth + matched + 1);
            overlay.Remove(path);
            overlay.Store(existingPath, relocated);
            PbtLeafNode newLeaf = new(key, value.Bytes.ToArray());
            overlay.Store(newPath, newLeaf);
            PbtBranchNode split = newDirection == 0
                ? new(common, newLeaf.Hash, relocated.Hash)
                : new(common, relocated.Hash, newLeaf.Hash);
            overlay.Store(path, split);
            return split.Hash;
        }

        int direction = key.GetBit(path.BitDepth + branch.Prefix.BitCount);
        PbtNodePath childPath = path.Append(branch.Prefix, direction);
        ValueHash256 childHash = direction == 0 ? branch.LeftHash : branch.RightHash;
        ValueHash256 replacementHash = Insert(overlay, childPath, childHash, key, value);
        PbtBranchNode replacementBranch = direction == 0
            ? new(branch.Prefix, replacementHash, branch.RightHash)
            : new(branch.Prefix, branch.LeftHash, replacementHash);
        overlay.Store(path, replacementBranch);
        return replacementBranch.Hash;
    }

    private static ValueHash256 Delete(
        Overlay overlay, PbtNodePath path, ValueHash256 expectedHash, PbtFullKey key, out bool removed)
    {
        PbtNode current = overlay.Load(path, expectedHash);
        if (current is PbtLeafNode leaf)
        {
            removed = leaf.Key.Equals(key);
            if (removed) overlay.Remove(path);
            return removed ? default : leaf.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int available = key.BitLength - path.BitDepth;
        if (available <= branch.Prefix.BitCount ||
            MatchingPrefixBits(branch.Prefix, key, path.BitDepth) != branch.Prefix.BitCount)
        {
            removed = false;
            return branch.Hash;
        }

        int direction = key.GetBit(path.BitDepth + branch.Prefix.BitCount);
        PbtNodePath childPath = path.Append(branch.Prefix, direction);
        ValueHash256 childHash = direction == 0 ? branch.LeftHash : branch.RightHash;
        ValueHash256 replacementChild = Delete(overlay, childPath, childHash, key, out removed);
        if (!removed) return branch.Hash;
        if (replacementChild != default)
        {
            PbtBranchNode replacement = direction == 0
                ? new(branch.Prefix, replacementChild, branch.RightHash)
                : new(branch.Prefix, branch.LeftHash, replacementChild);
            overlay.Store(path, replacement);
            return replacement.Hash;
        }

        int siblingDirection = 1 - direction;
        PbtNodePath siblingPath = path.Append(branch.Prefix, siblingDirection);
        ValueHash256 siblingHash = siblingDirection == 0 ? branch.LeftHash : branch.RightHash;
        PbtNode sibling = overlay.Load(siblingPath, siblingHash);
        overlay.Remove(siblingPath);
        PbtNode promoted = sibling is PbtBranchNode siblingBranch
            ? new PbtBranchNode(
                PbtBitPrefix.Concat(branch.Prefix, siblingDirection, siblingBranch.Prefix),
                siblingBranch.LeftHash,
                siblingBranch.RightHash)
            : sibling;
        overlay.Store(path, promoted);
        return promoted.Hash;
    }

    private static void ValidateBatchKeys(SortedDictionary<PbtFullKey, PbtWriteOperation> operations)
    {
        PbtFullKey? previous = null;
        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind == PbtWriteOperationKind.Delete) continue;
            if (previous is not null && previous.IsPrefixOf(operation.Key))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            previous = operation.Key;
        }
    }

    private static int MatchingPrefixBits(PbtBitPrefix prefix, PbtFullKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        int index = 0;
        while (index < count && prefix.GetBit(index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private static PbtBitPrefix Slice(PbtBitPrefix prefix, int start)
    {
        int count = prefix.BitCount - start;
        byte[] bytes = new byte[PbtBitPrefix.ByteCount(count)];
        for (int index = 0; index < count; index++)
        {
            if (prefix.GetBit(start + index) != 0) bytes[index >> 3] |= (byte)(1 << (7 - (index & 7)));
        }
        return new PbtBitPrefix(bytes, count);
    }

    private sealed class Overlay(IPbtStore store)
    {
        private readonly Dictionary<PbtFullKey, ValueHash256?> _leaves = [];
        private readonly Dictionary<PbtNodePath, byte[]?> _nodes = [];

        internal IReadOnlyList<PbtLeafMutation> LeafMutations
        {
            get
            {
                List<PbtLeafMutation> mutations = new(_leaves.Count);
                foreach ((PbtFullKey key, ValueHash256? value) in _leaves) mutations.Add(new(key, value));
                return mutations;
            }
        }

        internal IReadOnlyList<PbtNodeMutation> NodeMutations
        {
            get
            {
                List<PbtNodeMutation> mutations = new(_nodes.Count);
                foreach ((PbtNodePath path, byte[]? encoding) in _nodes) mutations.Add(new(path, encoding));
                return mutations;
            }
        }

        internal void SetLeaf(PbtFullKey key, ValueHash256? value) => _leaves[key] = value;

        internal PbtNode Load(PbtNodePath path, in ValueHash256 expectedHash)
        {
            byte[] encoding = (_nodes.TryGetValue(path, out byte[]? staged) ? staged : store.GetNode(path))
                ?? throw new InvalidDataException("A referenced PBT node is missing.");
            PbtNode node = PbtNodeCodec.Decode(encoding);
            if (node.Hash != expectedHash) throw new InvalidDataException("A persisted PBT node hash does not match its reference.");
            return node;
        }

        internal void Store(PbtNodePath path, PbtNode node) => _nodes[path] = PbtNodeCodec.Encode(node);

        internal void Remove(PbtNodePath path) => _nodes[path] = null;
    }
}
