// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Altair;

/// <summary>Altair <c>BeaconState</c>.</summary>
[SszContainer]
public partial class BeaconState : Nethermind.BeaconChain.Legacy.Bellatrix.BeaconState
{
    [SszIgnore]
    public new Bellatrix.ExecutionPayloadHeader? LatestExecutionPayloadHeader { get => base.LatestExecutionPayloadHeader; set => base.LatestExecutionPayloadHeader = value; }
}
