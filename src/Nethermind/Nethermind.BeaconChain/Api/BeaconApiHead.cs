// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api;

/// <summary>A consistent view of the published head and fork choice for one request.</summary>
internal sealed class BeaconApiHead
{
    private readonly FrozenHead _status;
    public IBeaconChainStatusSource StatusSource => _status;
    public ForkChoiceSnapshot? ForkChoiceSnapshot { get; }

    private BeaconApiHead(HeadSnapshot snapshot, ForkChoiceSnapshot? forkChoice)
    {
        _status = new FrozenHead(snapshot);
        ForkChoiceSnapshot = forkChoice;
    }

    public static BeaconApiHead Capture(IBeaconChainStatusSource source, HeadSnapshotHolder? heads = null, ForkChoiceSnapshotHolder? forkChoice = null)
    {
        if (heads?.Current is { } snapshot) return new(snapshot, snapshot.ForkChoice);
        (StatusMessageV2 status, Hash256? root) = source.CurrentHead;
        return new(new HeadSnapshot(status, root, source.JustifiedRoot, source.ExecutionInSync), heads is null ? forkChoice?.Current : null);
    }

    internal ForkedBeaconState? GetHeldState(Hash256 blockRoot, string stateId) =>
        GetCheckpointState(stateId)?.State ?? _status.GetState(blockRoot);

    internal CheckpointStateSnapshot? GetCheckpointState(string stateId) => _status.GetCheckpointState(stateId);

    private sealed class FrozenHead(HeadSnapshot snapshot) : IBeaconChainStatusSource
    {
        internal ForkedBeaconState? GetState(Hash256 blockRoot) => snapshot.GetState(blockRoot);
        internal CheckpointStateSnapshot? GetCheckpointState(string stateId)
        {
            if (stateId == "justified") return snapshot.JustifiedState;
            if (stateId == "finalized") return snapshot.FinalizedState;
            if (ulong.TryParse(stateId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong slot))
            {
                if (snapshot.JustifiedState is { } justifiedAtSlot && justifiedAtSlot.State.Slot == slot) return justifiedAtSlot;
                if (snapshot.FinalizedState is { } finalizedAtSlot && finalizedAtSlot.State.Slot == slot) return finalizedAtSlot;
            }
            if (!HexConvert.TryParseHash(stateId, out Hash256? root)) return null;
            if (snapshot.JustifiedState is { } justified && justified.StateRoot == root) return justified;
            return snapshot.FinalizedState is { } finalized && finalized.StateRoot == root ? finalized : null;
        }
        public StatusMessageV2 CurrentStatus => snapshot.Status;
        public Hash256 JustifiedRoot => snapshot.JustifiedRoot;
        public bool ExecutionInSync => snapshot.ExecutionInSync;
        public (StatusMessageV2 Status, Hash256? FullHeadRoot) CurrentHead => (snapshot.Status, snapshot.FullHeadRoot);
    }
}
