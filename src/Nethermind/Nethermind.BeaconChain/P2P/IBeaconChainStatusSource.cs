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
}

/// <summary>
/// A settable <see cref="IBeaconChainStatusSource"/>; the sync orchestrator updates it as the chain
/// advances. Until then it reports zero head/finalized roots with the wall-clock fork digest.
/// </summary>
public class BeaconChainStatusHolder(BeaconChainSpec spec, ITimestamper timestamper) : IBeaconChainStatusSource
{
    private volatile StatusMessageV2? _current;

    public StatusMessageV2 CurrentStatus
    {
        get => _current ?? new StatusMessageV2
        {
            ForkDigest = ForkDigest.Compute(spec, spec.GetEpoch(spec.GetSlotAtTime((ulong)timestamper.UnixTime.Seconds))),
            FinalizedRoot = Hash256.Zero,
            HeadRoot = Hash256.Zero,
        };
        set => _current = value;
    }
}
