// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules.DebugModule;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Projects the native migration state into debug telemetry.</summary>
/// <remarks>
/// Before activation the header commits to the MPT and PBT is the shadow, so <see cref="GetShadowRoot"/> reports
/// the EIP-8297 root PBT computed for the block and the binary direction follows the head. Through the transition
/// window the header commits to PBT, the binary direction parks at the activation parent and the Merkle direction
/// reports the MPT root <see cref="MerkleShadowFollower"/> computed for the block.
/// </remarks>
internal sealed class MigrationTelemetry(IBlockTree blockTree, IPbtDbManager manager, PbtBalFollowerScheduler scheduler, IMerkleShadowFollower merkle, ISpecProvider specProvider)
    : IMigrationTelemetry
{
    private BlockHeader? _activationParent;

    public MigrationProgressForRpc GetProgress()
    {
        if (MigrationActivation.IsFinal(blockTree, specProvider)) return new("done", null, null);
        BlockHeader? head = blockTree.Head?.Header;
        if (head is not null && specProvider.GetSpec(head).IsEip8347Enabled)
        {
            BlockHeader? parent = ActivationParent(head);
            MigrationDirectionForRpc? parked = parent is null ? null : new("parked", parent.Number, parent.Hash!, PbtRoot(parent), "");
            return new("running", parked, Direction(head, merkle.Cursor, merkle.Error));
        }
        return new("running", Direction(head, scheduler.Cursor, scheduler.Error), null);
    }

    public Hash256? GetShadowRoot(Hash256 blockHash)
    {
        BlockHeader? header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
        if (header is null) return null;
        if (specProvider.GetSpec(header).IsEip8347Enabled) return merkle.GetShadowRoot(blockHash);
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(header));
        return bundle?.TreeRoot.ToHash256();
    }

    private static MigrationDirectionForRpc? Direction(BlockHeader? head, PbtFollowerCursor? cursor, string? error)
    {
        if (cursor is null) return null;
        bool synced = head?.Hash == cursor.Hash;
        string phase = synced ? "synced" : error is null ? "following" : "stalled";
        return new(phase, cursor.Number, cursor.Hash, cursor.TreeRoot, synced ? "" : error ?? "");
    }

    private Hash256 PbtRoot(BlockHeader header)
    {
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(header));
        return (bundle?.TreeRoot ?? default).ToHash256();
    }

    // The activation parent only changes on a reorg across the boundary, so keep it while it stays canonical.
    private BlockHeader? ActivationParent(BlockHeader head)
    {
        BlockHeader? cached = _activationParent;
        if (cached is not null && blockTree.FindHeader(cached.Number)?.Hash == cached.Hash
            && blockTree.FindHeader(cached.Number + 1) is { } child && specProvider.GetSpec(child).IsEip8347Enabled)
            return cached;
        return _activationParent = MigrationActivation.FindActivationParent(blockTree, specProvider, head);
    }
}
