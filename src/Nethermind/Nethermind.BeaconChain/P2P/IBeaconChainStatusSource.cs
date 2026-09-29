// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Provides the node's current chain status advertised over the eth2 <c>status</c> protocol.</summary>
public interface IBeaconChainStatusSource
{
    StatusMessageV2 CurrentStatus { get; }

    /// <summary>Root of the head's justified checkpoint; <see cref="Hash256.Zero"/> until the first head step.</summary>
    Hash256 JustifiedRoot { get; }

    /// <summary>Whether the execution layer has confirmed the current head VALID; false until it has, which is the safe direction to be wrong in.</summary>
    bool ExecutionInSync { get; }

    /// <summary>The current status and, read together with it, the head root when <c>get_head</c> resolved it <c>PAYLOAD_STATUS_FULL</c> (<c>null</c> when the head is EMPTY or not yet known).</summary>
    (StatusMessageV2 Status, Hash256? FullHeadRoot) CurrentHead => (CurrentStatus, null);
}

/// <summary>
/// A settable <see cref="IBeaconChainStatusSource"/>; the sync orchestrator updates it as the chain
/// advances. Until then it reports zero head/finalized roots with the wall-clock fork digest.
/// </summary>
public class BeaconChainStatusHolder(BeaconChainSpec spec, ITimestamper timestamper) : IBeaconChainStatusSource
{
    private sealed record HeadSnapshot(StatusMessageV2 Status, Hash256? FullHeadRoot);

    private volatile HeadSnapshot? _head;
    private volatile Hash256 _justifiedRoot = Hash256.Zero;
    private volatile bool _executionInSync;

    public StatusMessageV2 CurrentStatus
    {
        get => _head?.Status ?? new StatusMessageV2
        {
            ForkDigest = ForkDigest.Compute(spec, spec.GetEpoch(spec.GetSlotAtTime((ulong)timestamper.UnixTime.Seconds))),
            FinalizedRoot = Hash256.Zero,
            HeadRoot = Hash256.Zero,
        };
        set => _head = new HeadSnapshot(value, null);
    }

    public (StatusMessageV2 Status, Hash256? FullHeadRoot) CurrentHead => _head is { } head ? (head.Status, head.FullHeadRoot) : (CurrentStatus, null);

    /// <summary>Replaces the status and the FULL head root in one step, so a reader never pairs a head with another head's payload status.</summary>
    public void Publish(StatusMessageV2 status, Hash256? fullHeadRoot) => _head = new HeadSnapshot(status, fullHeadRoot);

    public Hash256 JustifiedRoot
    {
        get => _justifiedRoot;
        set => _justifiedRoot = value;
    }

    public bool ExecutionInSync
    {
        get => _executionInSync;
        set => _executionInSync = value;
    }
}
