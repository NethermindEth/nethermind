// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore
{
    public byte[]? GetNode(PbtNodeLocator locator) => bundle.GetNode(locator);

    public void Apply(
        in ValueHash256 newRoot,
        IReadOnlyList<PbtLeafMutation> leafMutations,
        IReadOnlyList<PbtNodeMutation> nodeMutations)
        => bundle.ApplyTreeMutations(leafMutations, nodeMutations);
}
