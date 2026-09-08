// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore
{
    public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey) => bundle.GetNodeGroup(groupKey);

    public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload) => bundle.SetNodeGroup(groupKey, payload);
}
