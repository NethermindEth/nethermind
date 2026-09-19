// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Network;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Satisfies the discv5 node source, which needs an <see cref="IForkInfo"/> the consensus network has no equivalent of.</summary>
/// <remarks>
/// Beacon records carry <c>eth2</c> rather than <c>eth</c>, and <see cref="ForkInfoExtensions.IsNodeRecordForkCompatible"/>
/// short-circuits to compatible before consulting this type, so only <see cref="IsForkIdCompatible"/> is ever reached.
/// The rest throw: an execution-layer fork schedule has no meaning on a separate consensus distributed hash table,
/// so a caller that needs one is asking the wrong service and should fail loudly.
/// </remarks>
internal sealed class PermissiveForkInfo : IForkInfo
{
    public static PermissiveForkInfo Instance { get; } = new();

    public bool IsForkIdCompatible(ForkId peerId) => true;

    public ForkId GetForkId(ulong headNumber, ulong headTimestamp) => throw new NotSupportedException();

    public Nethermind.Network.ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head) => throw new NotSupportedException();

    public ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head) => throw new NotSupportedException();
}
