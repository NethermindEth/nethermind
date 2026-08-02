// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal enum PbtOperationKind : byte
{
    Set,
    Delete,
}

internal readonly record struct PbtOperation
{
    private PbtOperation(PbtFullKey key, byte[]? value, PbtOperationKind kind)
    {
        Key = key;
        Value = value;
        Kind = kind;
    }

    public PbtFullKey Key { get; }
    public byte[]? Value { get; }
    public PbtOperationKind Kind { get; }

    public static PbtOperation Set(PbtFullKey key, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        return new PbtOperation(key, value.ToArray(), PbtOperationKind.Set);
    }

    public static PbtOperation Delete(PbtFullKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new PbtOperation(key, null, PbtOperationKind.Delete);
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

/// <summary>Correctness-first full-key store for a single canonical EIP-8297 root.</summary>
internal sealed class PbtCanonicalStore
{
    private readonly SortedDictionary<PbtFullKey, byte[]> _leaves = [];
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
        ApplyValidated(operation);
        RebuildNodes();
    }

    public void Apply(PbtMutationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return;

        SortedDictionary<PbtFullKey, PbtOperation> finalOperations = [];
        foreach (PbtOperation operation in batch.Operations) finalOperations[operation.Key] = operation;
        ValidateResultingKeys(finalOperations);
        foreach (PbtOperation operation in finalOperations.Values) ApplyUnchecked(operation);
        RebuildNodes();
    }

    public IEnumerable<KeyValuePair<PbtFullKey, byte[]>> Enumerate(PbtFullKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        foreach (KeyValuePair<PbtFullKey, byte[]> entry in _leaves)
        {
            if (prefix.IsPrefixOf(entry.Key)) yield return new KeyValuePair<PbtFullKey, byte[]>(entry.Key, (byte[])entry.Value.Clone());
        }
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

    private void ApplyValidated(PbtOperation operation)
    {
        if (operation.Kind == PbtOperationKind.Set)
        {
            ValidateSetKey(operation.Key, null);
        }
        ApplyUnchecked(operation);
    }

    private void ApplyUnchecked(PbtOperation operation)
    {
        if (operation.Kind == PbtOperationKind.Delete)
        {
            _leaves.Remove(operation.Key);
        }
        else
        {
            _leaves[operation.Key] = (byte[])operation.Value!.Clone();
        }
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

    private void ValidateSetKey(PbtFullKey key, PbtFullKey? ignored)
    {
        foreach (PbtFullKey existing in _leaves.Keys)
        {
            if (!existing.Equals(key) && !existing.Equals(ignored) && (existing.IsPrefixOf(key) || key.IsPrefixOf(existing)))
            {
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            }
        }
    }

    private void RebuildNodes()
    {
        Dictionary<PbtNodeLocator, byte[]> nodes = [];
        if (_leaves.Count == 0)
        {
            _nodes = nodes;
            RootHash = default;
            return;
        }

        KeyValuePair<PbtFullKey, byte[]>[] entries = [.. _leaves];
        RootHash = Build(entries, 0, entries.Length, 0, nodes);
        _nodes = nodes;
    }

    private static ValueHash256 Build(KeyValuePair<PbtFullKey, byte[]>[] entries, int start, int end, int depth,
        Dictionary<PbtNodeLocator, byte[]> nodes)
    {
        PbtFullKey representative = entries[start].Key;
        PbtNodeLocator locator = PbtNodeLocator.FromKey(representative, depth);
        if (end - start == 1)
        {
            PbtLeafNode leaf = new(representative, (byte[])entries[start].Value.Clone());
            nodes.Add(locator, PbtNodeCodec.Encode(leaf));
            return leaf.Hash;
        }

        int differingBit = representative.FirstDifferingBit(entries[end - 1].Key, depth);
        if (differingBit == Math.Min(representative.BitLength, entries[end - 1].Key.BitLength))
        {
            throw new InvalidOperationException("Tree keys must be prefix-free.");
        }
        int split = start + 1;
        while (split < end && entries[split].Key.GetBit(differingBit) == 0) split++;
        ValueHash256 left = Build(entries, start, split, differingBit + 1, nodes);
        ValueHash256 right = Build(entries, split, end, differingBit + 1, nodes);
        PbtBranchNode branch = new(PbtBitPrefix.FromKey(representative, depth, differingBit - depth), left, right);
        nodes.Add(locator, PbtNodeCodec.Encode(branch));
        return branch.Hash;
    }
}
