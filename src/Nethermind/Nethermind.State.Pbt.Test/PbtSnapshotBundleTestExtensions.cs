// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

internal static class PbtSnapshotBundleTestExtensions
{
    public static void SetSlot(this PbtSnapshotBundle bundle, Address address, in UInt256 slot, in EvmWord value) =>
        bundle.SetSlot(address, PbtKeyDerivation.AddressKeyHash(address), slot, in value);

    public static PbtSnapshot CollectSnapshot(this PbtSnapshotBundle bundle, in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        PbtSnapshot snapshot = bundle.CollectSnapshot(from, to, treeRoot, out PbtTransientResource retired);
        retired.ReleaseLease();
        return snapshot;
    }

    public static bool ShouldPrewarm(this PbtTransientResource resource, Address address, UInt256? slot = null) =>
        resource.ShouldPrewarm(new ValueAddress(address.Bytes), slot);

    /// <summary>Retains one group in <paramref name="cache"/> by staging it in a throwaway transient resource and folding that in.</summary>
    public static void Add<TPath>(this PbtTrieNodeCache cache, in ValueHash256 groupHash, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
    {
        using PbtTransientResource staged = new(prewarmCapacity: 1, nodeGroupCapacity: 1);
        staged.NodeGroups.Set(groupHash, path, payload);
        cache.Add(staged);
    }
}
