// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A correctness-first canonical EIP-8297 tree over owned complete keys.</summary>
public sealed class PbtCanonicalTree
{
    private readonly PbtCanonicalStore _store = new();

    public int Count => _store.LeafCount;
    public ValueHash256 RootHash => _store.RootHash;

    public void Set(PbtFullKey key, ReadOnlySpan<byte> value) => _store.Apply(PbtOperation.Set(key, value));

    public bool Delete(PbtFullKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Span<byte> existing = stackalloc byte[32];
        bool found = _store.TryGet(key, existing);
        if (found) _store.Apply(PbtOperation.Delete(key));
        return found;
    }

    public bool TryGet(PbtFullKey key, Span<byte> value) => _store.TryGet(key, value);

    public static ValueHash256 Rebuild(IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> entries) =>
        RebuildWithNodes(entries).RootHash;

    /// <summary>Rebuilds a canonical tree and returns its root and owned encoded nodes.</summary>
    public static PbtCanonicalBuildResult RebuildWithNodes(IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        PbtCanonicalTree tree = new();
        foreach ((PbtFullKey key, ValueHash256 value) in entries) tree.Set(key, value.Bytes);
        return new PbtCanonicalBuildResult(tree.RootHash, tree._store.ExportNodes());
    }
}
