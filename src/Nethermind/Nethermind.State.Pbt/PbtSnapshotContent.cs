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
    internal readonly ConcurrentDictionary<PbtFullKey, ValueHash256?> Leaves = new();
    internal readonly ConcurrentDictionary<PbtFullKey, byte[]?> Nodes = new();
    internal readonly ConcurrentDictionary<ValueHash256, ulong?> CodeReferences = new();

    internal void SetLeaf(PbtFullKey key, ValueHash256? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        Leaves[key] = value;
    }

    internal bool TryGetLeaf(PbtFullKey key, out ValueHash256? value) => Leaves.TryGetValue(key, out value);

    internal void SetNode(PbtFullKey locator, ReadOnlySpan<byte> encoding) =>
        Nodes[locator] = encoding.IsEmpty ? null : encoding.ToArray();

    internal bool TryGetNode(PbtFullKey locator, out byte[]? encoding) => Nodes.TryGetValue(locator, out encoding);

    internal void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) => CodeReferences[codeHash] = referenceCount;

    internal bool TryGetCodeReference(in ValueHash256 codeHash, out ulong? referenceCount) =>
        CodeReferences.TryGetValue(codeHash, out referenceCount);

    public void Reset()
    {
        Leaves.NoLockClear();
        Nodes.NoLockClear();
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

        foreach ((PbtFullKey locator, byte[]? node) in Nodes)
        {
            nodeBytes += locator.Length + (node?.Length ?? 0);
        }

        long codeReferenceBytes = CodeReferences.Count * (ValueHash256.MemorySize + sizeof(ulong));
        return new PbtSnapshotPayloadSize(leafBytes, nodeBytes, codeReferenceBytes);
    }

    public void Dispose() => Reset();
}

internal readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node, long CodeReference);
