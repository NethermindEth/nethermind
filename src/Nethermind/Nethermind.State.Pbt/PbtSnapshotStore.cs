// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore
{
    private readonly List<PbtLeafMutation> _leafMutations = [];

    public byte[]? GetNode(PbtNodePath path) => bundle.GetNode(path);

    public PbtNodeGroupPayload? GetNodeGroup(PbtNodePath groupKey) => bundle.GetNodeGroup(groupKey);

    public void SetLeaf(PbtFullKey key, ValueHash256? value) => _leafMutations.Add(new(key, value));

    public void SetNode(PbtNodePath path, byte[]? encoding)
    {
        bundle.ApplyTreeMutations(_leafMutations, [new(path, encoding)]);
        _leafMutations.Clear();
    }
}
