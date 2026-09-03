// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal enum PbtOperationKind : byte { Set, Delete }

internal readonly record struct PbtOperation
{
    private PbtOperation(PbtFullKey key, byte[]? value, PbtOperationKind kind) => (Key, Value, Kind) = (key, value, kind);
    public PbtFullKey Key { get; }
    public byte[]? Value { get; }
    public PbtOperationKind Kind { get; }
    public static PbtOperation Set(PbtFullKey key, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        return new(key, value.ToArray(), PbtOperationKind.Set);
    }
    public static PbtOperation Delete(PbtFullKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key, null, PbtOperationKind.Delete);
    }
}

internal sealed class PbtMutationBatch
{
    private readonly List<PbtOperation> _operations = [];
    public int Count => _operations.Count;
    public IReadOnlyList<PbtOperation> Operations => _operations;
    public void Set(PbtFullKey key, ReadOnlySpan<byte> value) => _operations.Add(PbtOperation.Set(key, value));
    public void Delete(PbtFullKey key) => _operations.Add(PbtOperation.Delete(key));
}

/// <summary>Incremental full-key store for a single canonical EIP-8297 root.</summary>
internal sealed class PbtCanonicalStore
{
    private static readonly PbtNodeLocator RootLocator = new([], 0);
    private SortedDictionary<PbtFullKey, byte[]> _leaves = [];
    private Dictionary<PbtNodeLocator, byte[]> _nodes = [];

    public ValueHash256 RootHash { get; private set; }
    public int LeafCount => _leaves.Count;
    public int NodeCount => _nodes.Count;

    public bool TryGet(PbtFullKey key, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (destination.Length < 32) throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination));
        if (!_leaves.TryGetValue(key, out byte[]? value)) return false;
        value.CopyTo(destination);
        return true;
    }

    public void Apply(PbtOperation operation)
    {
        PbtMutationBatch batch = new();
        if (operation.Kind == PbtOperationKind.Delete) batch.Delete(operation.Key);
        else batch.Set(operation.Key, operation.Value!);
        Apply(batch);
    }

    public void Apply(PbtMutationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return;

        SortedDictionary<PbtFullKey, PbtOperation> final = [];
        foreach (PbtOperation operation in batch.Operations) final[operation.Key] = operation;
        ValidateResultingKeys(final);

        SortedDictionary<PbtFullKey, byte[]> leaves = new(_leaves);
        Dictionary<PbtNodeLocator, byte[]> nodes = new(_nodes);
        ValueHash256 root = RootHash;
        foreach (PbtOperation operation in final.Values)
        {
            if (operation.Kind != PbtOperationKind.Delete) continue;
            if (!leaves.Remove(operation.Key)) continue;
            root = Delete(nodes, RootLocator, root, operation.Key, out _);
        }
        foreach (PbtOperation operation in final.Values)
        {
            if (operation.Kind != PbtOperationKind.Set) continue;
            leaves[operation.Key] = (byte[])operation.Value!.Clone();
            root = Insert(nodes, RootLocator, root, operation.Key, operation.Value);
        }

        _leaves = leaves;
        _nodes = nodes;
        RootHash = root;
    }

    public IEnumerable<KeyValuePair<PbtFullKey, byte[]>> Enumerate(PbtFullKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        foreach (KeyValuePair<PbtFullKey, byte[]> entry in _leaves)
            if (prefix.IsPrefixOf(entry.Key)) yield return new(entry.Key, (byte[])entry.Value.Clone());
    }

    internal bool TryGetNode(PbtNodeLocator locator, out byte[]? encoding)
    {
        if (_nodes.TryGetValue(locator, out byte[]? stored))
        {
            encoding = (byte[])stored.Clone();
            return true;
        }
        encoding = null;
        return false;
    }

    internal PbtEncodedNode[] ExportNodes()
    {
        PbtEncodedNode[] result = new PbtEncodedNode[_nodes.Count];
        int index = 0;
        foreach ((PbtNodeLocator locator, byte[] encoding) in _nodes)
        {
            result[index++] = new PbtEncodedNode(locator.Encode(), encoding);
        }

        Array.Sort(result, static (left, right) => left.LocatorEncoding.Span.SequenceCompareTo(right.LocatorEncoding.Span));
        return result;
    }

    private static ValueHash256 Insert(Dictionary<PbtNodeLocator, byte[]> nodes, PbtNodeLocator locator,
        ValueHash256 expectedHash, PbtFullKey key, byte[] value)
    {
        if (expectedHash == default)
        {
            PbtLeafNode leaf = new(key, (byte[])value.Clone());
            Store(nodes, locator, leaf);
            return leaf.Hash;
        }

        PbtCanonicalNode current = Load(nodes, locator, expectedHash);
        if (current is PbtLeafNode existingLeaf)
        {
            if (existingLeaf.Key.Equals(key))
            {
                PbtLeafNode replacement = new(key, (byte[])value.Clone());
                Store(nodes, locator, replacement);
                return replacement.Hash;
            }

            int differingBit = existingLeaf.Key.FirstDifferingBit(key, locator.BitDepth);
            PbtBitPrefix prefix = PbtBitPrefix.FromKey(key, locator.BitDepth, differingBit - locator.BitDepth);
            int existingDirection = existingLeaf.Key.GetBit(differingBit);
            PbtNodeLocator existingLocator = PbtNodeLocator.FromKey(existingLeaf.Key, differingBit + 1);
            PbtNodeLocator newLocator = PbtNodeLocator.FromKey(key, differingBit + 1);
            nodes.Remove(locator);
            Store(nodes, existingLocator, existingLeaf);
            PbtLeafNode newLeaf = new(key, (byte[])value.Clone());
            Store(nodes, newLocator, newLeaf);
            PbtBranchNode split = existingDirection == 0
                ? new(prefix, existingLeaf.Hash, newLeaf.Hash)
                : new(prefix, newLeaf.Hash, existingLeaf.Hash);
            Store(nodes, locator, split);
            return split.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int matched = MatchingPrefixBits(branch.Prefix, key, locator.BitDepth);
        if (matched < branch.Prefix.BitCount)
        {
            PbtBitPrefix common = PbtBitPrefix.FromKey(key, locator.BitDepth, matched);
            int existingDirection = branch.Prefix.GetBit(matched);
            PbtBitPrefix remaining = Slice(branch.Prefix, matched + 1);
            PbtBranchNode relocated = new(remaining, branch.LeftHash, branch.RightHash);
            PbtNodeLocator existingLocator = locator.Append(common, existingDirection);
            int newDirection = key.GetBit(locator.BitDepth + matched);
            PbtNodeLocator newLocator = PbtNodeLocator.FromKey(key, locator.BitDepth + matched + 1);
            nodes.Remove(locator);
            Store(nodes, existingLocator, relocated);
            PbtLeafNode newLeaf = new(key, (byte[])value.Clone());
            Store(nodes, newLocator, newLeaf);
            PbtBranchNode split = newDirection == 0
                ? new(common, newLeaf.Hash, relocated.Hash)
                : new(common, relocated.Hash, newLeaf.Hash);
            Store(nodes, locator, split);
            return split.Hash;
        }

        int direction = key.GetBit(locator.BitDepth + branch.Prefix.BitCount);
        PbtNodeLocator childLocator = locator.Append(branch.Prefix, direction);
        ValueHash256 childHash = direction == 0 ? branch.LeftHash : branch.RightHash;
        ValueHash256 replacementHash = Insert(nodes, childLocator, childHash, key, value);
        PbtBranchNode replacementBranch = direction == 0
            ? new(branch.Prefix, replacementHash, branch.RightHash)
            : new(branch.Prefix, branch.LeftHash, replacementHash);
        Store(nodes, locator, replacementBranch);
        return replacementBranch.Hash;
    }

    private static ValueHash256 Delete(Dictionary<PbtNodeLocator, byte[]> nodes, PbtNodeLocator locator,
        ValueHash256 expectedHash, PbtFullKey key, out bool removed)
    {
        PbtCanonicalNode current = Load(nodes, locator, expectedHash);
        if (current is PbtLeafNode leaf)
        {
            removed = leaf.Key.Equals(key);
            if (removed) nodes.Remove(locator);
            return removed ? default : leaf.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        if (MatchingPrefixBits(branch.Prefix, key, locator.BitDepth) != branch.Prefix.BitCount)
        {
            removed = false;
            return branch.Hash;
        }
        int direction = key.GetBit(locator.BitDepth + branch.Prefix.BitCount);
        PbtNodeLocator childLocator = locator.Append(branch.Prefix, direction);
        ValueHash256 childHash = direction == 0 ? branch.LeftHash : branch.RightHash;
        ValueHash256 replacementChild = Delete(nodes, childLocator, childHash, key, out removed);
        if (!removed) return branch.Hash;
        if (replacementChild != default)
        {
            PbtBranchNode replacement = direction == 0
                ? new(branch.Prefix, replacementChild, branch.RightHash)
                : new(branch.Prefix, branch.LeftHash, replacementChild);
            Store(nodes, locator, replacement);
            return replacement.Hash;
        }

        int siblingDirection = 1 - direction;
        PbtNodeLocator siblingLocator = locator.Append(branch.Prefix, siblingDirection);
        ValueHash256 siblingHash = siblingDirection == 0 ? branch.LeftHash : branch.RightHash;
        PbtCanonicalNode sibling = Load(nodes, siblingLocator, siblingHash);
        nodes.Remove(siblingLocator);
        PbtCanonicalNode promoted = sibling is PbtBranchNode siblingBranch
            ? new PbtBranchNode(PbtBitPrefix.Concat(branch.Prefix, siblingDirection, siblingBranch.Prefix), siblingBranch.LeftHash, siblingBranch.RightHash)
            : sibling;
        Store(nodes, locator, promoted);
        return promoted.Hash;
    }

    private static PbtCanonicalNode Load(Dictionary<PbtNodeLocator, byte[]> nodes, PbtNodeLocator locator, ValueHash256 expectedHash)
    {
        if (!nodes.TryGetValue(locator, out byte[]? encoding)) throw new InvalidDataException("A referenced PBT node is missing.");
        PbtCanonicalNode node = PbtNodeCodec.Decode(encoding);
        if (node.Hash != expectedHash) throw new InvalidDataException("A persisted PBT node hash does not match its reference.");
        return node;
    }

    private static void Store(Dictionary<PbtNodeLocator, byte[]> nodes, PbtNodeLocator locator, PbtCanonicalNode node) =>
        nodes[locator] = PbtNodeCodec.Encode(node);

    private static int MatchingPrefixBits(PbtBitPrefix prefix, PbtFullKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        int i = 0;
        while (i < count && prefix.GetBit(i) == key.GetBit(keyOffset + i)) i++;
        return i;
    }

    private static PbtBitPrefix Slice(PbtBitPrefix prefix, int start)
    {
        int count = prefix.BitCount - start;
        byte[] bytes = new byte[PbtBitPrefix.ByteCount(count)];
        for (int i = 0; i < count; i++)
            if (prefix.GetBit(start + i) != 0) bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return new PbtBitPrefix(bytes, count);
    }

    private void ValidateResultingKeys(SortedDictionary<PbtFullKey, PbtOperation> operations)
    {
        SortedSet<PbtFullKey> resulting = [.. _leaves.Keys];
        foreach (PbtOperation operation in operations.Values)
        {
            if (operation.Kind == PbtOperationKind.Delete) resulting.Remove(operation.Key);
            else resulting.Add(operation.Key);
        }
        PbtFullKey? previous = null;
        foreach (PbtFullKey key in resulting)
        {
            if (previous is not null && previous.IsPrefixOf(key)) throw new ArgumentException("Tree keys must be prefix-free.");
            previous = key;
        }
    }
}
