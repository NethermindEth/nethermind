// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// A LOT of test rely on the fact that trie store will assume state is available as long as the state root is
/// empty tree even if the blocknumber is not -1. This does not work with flat. We will ignore it for now.
/// </summary>
/// <param name="flatDbManager"></param>
internal class FlatDbManagerTestCompat(IFlatDbManager flatDbManager) : IFlatDbManager
{
    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage)
    {
        IgnoreOnInvalidState(baseBlock);
        return flatDbManager.GatherSnapshotBundle(baseBlock, usage);
    }

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
    {
        IgnoreOnInvalidState(baseBlock);
        return flatDbManager.GatherReadOnlySnapshotBundle(baseBlock);
    }

    public bool HasStateForBlock(in StateId stateId)
    {
        IgnoreOnInvalidState(stateId);
        return flatDbManager.HasStateForBlock(stateId);
    }

    private void IgnoreOnInvalidState(StateId stateId)
    {
        if (stateId.StateRoot == Keccak.EmptyTreeHash && stateId.BlockNumber != -1 &&
            !flatDbManager.HasStateForBlock(stateId))
        {
            Assert.Ignore("Incompatible test");
        }
    }

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) => flatDbManager.AddSnapshot(snapshot, transientResource);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => flatDbManager.ReorgBoundaryReached += value;
        remove => flatDbManager.ReorgBoundaryReached -= value;
    }
}
