// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Atomically applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static class TrieUpdater
{
    private static readonly PbtNodeLocator RootLocator = new([], 0);

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
            root = Delete(overlay, RootLocator, root, operation.Key, out bool removed);
            if (removed) overlay.SetLeaf(operation.Key, null);
        }

        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind != PbtWriteOperationKind.Set) continue;
            root = Insert(overlay, RootLocator, root, operation.Key, operation.Value);
            overlay.SetLeaf(operation.Key, operation.Value);
        }

        store.Apply(root, overlay.LeafMutations, overlay.NodeMutations);
        return root;
    }

    private static ValueHash256 Insert(
        Overlay overlay, PbtNodeLocator locator, ValueHash256 expectedHash, PbtFullKey key, in ValueHash256 value)
    {
        if (expectedHash == default)
        {
            PbtLeafNode leaf = new(key, value.Bytes.ToArray());
            overlay.Store(locator, leaf);
            return leaf.Hash;
        }

        PbtNode current = overlay.Load(locator, expectedHash);
        if (current is PbtLeafNode existingLeaf)
        {
            if (existingLeaf.Key.Equals(key))
            {
                PbtLeafNode replacement = new(key, value.Bytes.ToArray());
                overlay.Store(locator, replacement);
                return replacement.Hash;
            }

            int differingBit = existingLeaf.Key.FirstDifferingBit(key, locator.BitDepth);
            if (differingBit == Math.Min(existingLeaf.Key.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            PbtBitPrefix prefix = PbtBitPrefix.FromKey(key, locator.BitDepth, differingBit - locator.BitDepth);
            int existingDirection = existingLeaf.Key.GetBit(differingBit);
            PbtNodeLocator existingLocator = PbtNodeLocator.FromKey(existingLeaf.Key, differingBit + 1);
            PbtNodeLocator newLocator = PbtNodeLocator.FromKey(key, differingBit + 1);
            overlay.Remove(locator);
            overlay.Store(existingLocator, existingLeaf);
            PbtLeafNode newLeaf = new(key, value.Bytes.ToArray());
            overlay.Store(newLocator, newLeaf);
            PbtBranchNode split = existingDirection == 0
                ? new(prefix, existingLeaf.Hash, newLeaf.Hash)
                : new(prefix, newLeaf.Hash, existingLeaf.Hash);
            overlay.Store(locator, split);
            return split.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int available = key.BitLength - locator.BitDepth;
        int matched = MatchingPrefixBits(branch.Prefix, key, locator.BitDepth);
        if (matched == available)
            throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
        if (matched < branch.Prefix.BitCount)
        {
            PbtBitPrefix common = PbtBitPrefix.FromKey(key, locator.BitDepth, matched);
            int existingDirection = branch.Prefix.GetBit(matched);
            PbtBranchNode relocated = new(Slice(branch.Prefix, matched + 1), branch.LeftHash, branch.RightHash);
            PbtNodeLocator existingLocator = locator.Append(common, existingDirection);
            int newDirection = key.GetBit(locator.BitDepth + matched);
            PbtNodeLocator newLocator = PbtNodeLocator.FromKey(key, locator.BitDepth + matched + 1);
            overlay.Remove(locator);
            overlay.Store(existingLocator, relocated);
            PbtLeafNode newLeaf = new(key, value.Bytes.ToArray());
            overlay.Store(newLocator, newLeaf);
            PbtBranchNode split = newDirection == 0
                ? new(common, newLeaf.Hash, relocated.Hash)
                : new(common, relocated.Hash, newLeaf.Hash);
            overlay.Store(locator, split);
            return split.Hash;
        }

        int direction = key.GetBit(locator.BitDepth + branch.Prefix.BitCount);
        PbtNodeLocator childLocator = locator.Append(branch.Prefix, direction);
        ValueHash256 childHash = direction == 0 ? branch.LeftHash : branch.RightHash;
        ValueHash256 replacementHash = Insert(overlay, childLocator, childHash, key, value);
        PbtBranchNode replacementBranch = direction == 0
            ? new(branch.Prefix, replacementHash, branch.RightHash)
            : new(branch.Prefix, branch.LeftHash, replacementHash);
        overlay.Store(locator, replacementBranch);
        return replacementBranch.Hash;
    }

    private static ValueHash256 Delete(
        Overlay overlay, PbtNodeLocator locator, ValueHash256 expectedHash, PbtFullKey key, out bool removed)
    {
        PbtNode current = overlay.Load(locator, expectedHash);
        if (current is PbtLeafNode leaf)
        {
            removed = leaf.Key.Equals(key);
            if (removed) overlay.Remove(locator);
            return removed ? default : leaf.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int available = key.BitLength - locator.BitDepth;
        if (available <= branch.Prefix.BitCount ||
            MatchingPrefixBits(branch.Prefix, key, locator.BitDepth) != branch.Prefix.BitCount)
        {
            removed = false;
            return branch.Hash;
        }

        int direction = key.GetBit(locator.BitDepth + branch.Prefix.BitCount);
        PbtNodeLocator childLocator = locator.Append(branch.Prefix, direction);
        ValueHash256 childHash = direction == 0 ? branch.LeftHash : branch.RightHash;
        ValueHash256 replacementChild = Delete(overlay, childLocator, childHash, key, out removed);
        if (!removed) return branch.Hash;
        if (replacementChild != default)
        {
            PbtBranchNode replacement = direction == 0
                ? new(branch.Prefix, replacementChild, branch.RightHash)
                : new(branch.Prefix, branch.LeftHash, replacementChild);
            overlay.Store(locator, replacement);
            return replacement.Hash;
        }

        int siblingDirection = 1 - direction;
        PbtNodeLocator siblingLocator = locator.Append(branch.Prefix, siblingDirection);
        ValueHash256 siblingHash = siblingDirection == 0 ? branch.LeftHash : branch.RightHash;
        PbtNode sibling = overlay.Load(siblingLocator, siblingHash);
        overlay.Remove(siblingLocator);
        PbtNode promoted = sibling is PbtBranchNode siblingBranch
            ? new PbtBranchNode(
                PbtBitPrefix.Concat(branch.Prefix, siblingDirection, siblingBranch.Prefix),
                siblingBranch.LeftHash,
                siblingBranch.RightHash)
            : sibling;
        overlay.Store(locator, promoted);
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
        private readonly Dictionary<PbtNodeLocator, byte[]?> _nodes = [];

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
                foreach ((PbtNodeLocator locator, byte[]? encoding) in _nodes) mutations.Add(new(locator, encoding));
                return mutations;
            }
        }

        internal void SetLeaf(PbtFullKey key, ValueHash256? value) => _leaves[key] = value;

        internal PbtNode Load(PbtNodeLocator locator, in ValueHash256 expectedHash)
        {
            byte[] encoding = (_nodes.TryGetValue(locator, out byte[]? staged) ? staged : store.GetNode(locator))
                ?? throw new InvalidDataException("A referenced PBT node is missing.");
            PbtNode node = PbtNodeCodec.Decode(encoding);
            if (node.Hash != expectedHash) throw new InvalidDataException("A persisted PBT node hash does not match its reference.");
            return node;
        }

        internal void Store(PbtNodeLocator locator, PbtNode node) => _nodes[locator] = PbtNodeCodec.Encode(node);

        internal void Remove(PbtNodeLocator locator) => _nodes[locator] = null;
    }
}
