// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Deneb;

/// <summary>Deneb <c>BeaconState</c>.</summary>
[SszContainer]
public partial class BeaconState : Nethermind.BeaconChain.Legacy.Electra.BeaconState
{
    [SszIgnore]
    public new ulong DepositRequestsStartIndex { get => base.DepositRequestsStartIndex; set => base.DepositRequestsStartIndex = value; }
    [SszIgnore]
    public new ulong DepositBalanceToConsume { get => base.DepositBalanceToConsume; set => base.DepositBalanceToConsume = value; }
    [SszIgnore]
    public new ulong ExitBalanceToConsume { get => base.ExitBalanceToConsume; set => base.ExitBalanceToConsume = value; }
    [SszIgnore]
    public new ulong EarliestExitEpoch { get => base.EarliestExitEpoch; set => base.EarliestExitEpoch = value; }
    [SszIgnore]
    public new ulong ConsolidationBalanceToConsume { get => base.ConsolidationBalanceToConsume; set => base.ConsolidationBalanceToConsume = value; }
    [SszIgnore]
    public new ulong EarliestConsolidationEpoch { get => base.EarliestConsolidationEpoch; set => base.EarliestConsolidationEpoch = value; }
    [SszIgnore]
    public new PendingDeposit[]? PendingDeposits { get => base.PendingDeposits; set => base.PendingDeposits = value; }
    [SszIgnore]
    public new PendingPartialWithdrawal[]? PendingPartialWithdrawals { get => base.PendingPartialWithdrawals; set => base.PendingPartialWithdrawals = value; }
    [SszIgnore]
    public new PendingConsolidation[]? PendingConsolidations { get => base.PendingConsolidations; set => base.PendingConsolidations = value; }
}
