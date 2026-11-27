// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Capella;

/// <summary>Capella <c>BeaconState</c>.</summary>
[SszContainer]
public partial class BeaconState : Nethermind.BeaconChain.Legacy.Deneb.BeaconState
{
    public new ExecutionPayloadHeader? LatestExecutionPayloadHeader
    {
        get => (ExecutionPayloadHeader?)base.LatestExecutionPayloadHeader;
        set => base.LatestExecutionPayloadHeader = value;
    }
}
