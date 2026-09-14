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
/// the EIP-8297 root PBT computed for the block. After activation flat is frozen, so there is no shadow.
/// </remarks>
internal sealed class MigrationTelemetry(IBlockTree blockTree, IPbtDbManager manager, PbtBalFollowerScheduler scheduler, ISpecProvider specProvider)
    : IMigrationTelemetry
{
    public MigrationProgressForRpc GetProgress()
    {
        if (IsActivationFinal()) return new("done", null, null);
        PbtFollowerCursor? cursor = scheduler.Cursor;
        if (cursor is null) return new("running", null, null);
        string? error = scheduler.Error;
        bool synced = blockTree.Head?.Hash == cursor.Hash;
        string phase = synced ? "synced" : error is null ? "following" : "stalled";
        return new("running", new(phase, cursor.Number, cursor.Hash, cursor.TreeRoot, synced ? "" : error ?? ""), null);
    }

    public Hash256? GetShadowRoot(Hash256 blockHash)
    {
        BlockHeader? header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
        if (header is null || specProvider.GetSpec(header).IsEip8347Enabled) return null;
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(header));
        return bundle?.TreeRoot.ToHash256();
    }

    private bool IsActivationFinal() =>
        blockTree.FinalizedHash is { } finalizedHash && finalizedHash != Hash256.Zero &&
        blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.None) is { } finalized && specProvider.GetSpec(finalized).IsEip8347Enabled;
}
