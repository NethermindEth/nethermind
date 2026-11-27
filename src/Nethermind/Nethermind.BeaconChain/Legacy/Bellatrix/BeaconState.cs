// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Bellatrix;

/// <summary>Bellatrix <c>BeaconState</c>.</summary>
[SszContainer]
public partial class BeaconState : Nethermind.BeaconChain.Legacy.Capella.BeaconState
{
    public new ExecutionPayloadHeader? LatestExecutionPayloadHeader
    {
        get => (ExecutionPayloadHeader?)base.LatestExecutionPayloadHeader;
        set => base.LatestExecutionPayloadHeader = value;
    }
    [SszIgnore]
    public new ulong NextWithdrawalIndex { get => base.NextWithdrawalIndex; set => base.NextWithdrawalIndex = value; }
    [SszIgnore]
    public new ulong NextWithdrawalValidatorIndex { get => base.NextWithdrawalValidatorIndex; set => base.NextWithdrawalValidatorIndex = value; }
    [SszIgnore]
    public new HistoricalSummary[]? HistoricalSummaries { get => base.HistoricalSummaries; set => base.HistoricalSummaries = value; }
}
