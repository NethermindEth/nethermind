// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore
{
    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash) => bundle.GetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), groupHash);

    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => bundle.SetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), groupHash, payload);
}
