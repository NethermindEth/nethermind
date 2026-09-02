// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>One immutable-at-seal diff layer of canonical EIP-8297 leaves, compressed nodes, and code references.</summary>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    private readonly Lock _treeLock = new();

    internal ConcurrentDictionary<PbtFullKey, ValueHash256?> Leaves = new();
    internal ConcurrentDictionary<PbtNodePath, byte[]?> Nodes = new();
    internal readonly ConcurrentDictionary<ValueHash256, ulong?> CodeReferences = new();

    internal void SetLeaf(PbtFullKey key, ValueHash256? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_treeLock) Leaves[key] = value is null || value.Value == default ? null : value;
    }

    internal bool TryGetLeaf(PbtFullKey key, out ValueHash256? value)
    {
        lock (_treeLock) return Leaves.TryGetValue(key, out value);
    }

    internal void SetNode(PbtNodePath path, ReadOnlySpan<byte> encoding)
    {
        byte[]? ownedEncoding = encoding.IsEmpty ? null : encoding.ToArray();
        lock (_treeLock) Nodes[path] = ownedEncoding;
    }

    internal bool TryGetNode(PbtNodePath path, out byte[]? encoding)
    {
        lock (_treeLock) return Nodes.TryGetValue(path, out encoding);
    }

    internal bool ApplyNodeGroupDeltas(PbtNodePath groupKey, ReadOnlyMemory<byte>[] encodings, bool[] present)
    {
        bool changed = false;
        lock (_treeLock)
        {
            for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            {
                if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
                if (!Nodes.TryGetValue(path, out byte[]? encoding)) continue;

                changed = true;
                if (encoding is null)
                {
                    present[position] = false;
                    encodings[position] = default;
                    continue;
                }

                PbtNodeGroupCodec.ValidateNodeEncoding(path, encoding);
                encodings[position] = encoding;
                present[position] = true;
            }
        }
        return changed;
    }

    internal void ApplyTreeMutations(
        IReadOnlyList<PbtLeafMutation> leafMutations,
        IReadOnlyList<PbtNodeMutation> nodeMutations)
    {
        lock (_treeLock)
        {
            ConcurrentDictionary<PbtFullKey, ValueHash256?> leaves = new(Leaves);
            ConcurrentDictionary<PbtNodePath, byte[]?> nodes = new(Nodes);
            foreach (PbtLeafMutation mutation in leafMutations)
                leaves[mutation.Key] = mutation.Value is null || mutation.Value.Value == default ? null : mutation.Value;
            foreach (PbtNodeMutation mutation in nodeMutations)
                nodes[mutation.Path] = mutation.Encoding is null ? null : (byte[])mutation.Encoding.Clone();
            Leaves = leaves;
            Nodes = nodes;
        }
    }

    internal void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) => CodeReferences[codeHash] = referenceCount;

    internal bool TryGetCodeReference(in ValueHash256 codeHash, out ulong? referenceCount) =>
        CodeReferences.TryGetValue(codeHash, out referenceCount);

    public void Reset()
    {
        lock (_treeLock)
        {
            Leaves.NoLockClear();
            Nodes.NoLockClear();
        }
        CodeReferences.NoLockClear();
    }

    internal PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = 0;
        long nodeBytes = 0;
        foreach ((PbtFullKey key, ValueHash256? value) in Leaves)
        {
            leafBytes += key.Length + (value is null ? 0 : ValueHash256.MemorySize);
        }

        foreach ((PbtNodePath path, byte[]? node) in Nodes)
        {
            nodeBytes += path.Encode().Length + (node?.Length ?? 0);
        }

        long codeReferenceBytes = CodeReferences.Count * (ValueHash256.MemorySize + sizeof(ulong));
        return new PbtSnapshotPayloadSize(leafBytes, nodeBytes, codeReferenceBytes);
    }

    public void Dispose() => Reset();
}

internal readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node, long CodeReference);
