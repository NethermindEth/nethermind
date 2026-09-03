// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore
{
    public byte[]? GetNode(PbtNodePath path) => bundle.GetNode(path);

    public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey) => bundle.GetNodeGroup(groupKey);

    public void SetLeaf(PbtFullKey key, ValueHash256? value) => bundle.SetLeaf(key, value);

    public void SetNode(PbtNodePath path, byte[]? encoding) => bundle.ApplyTreeMutations([], [new(path, encoding)]);
}
