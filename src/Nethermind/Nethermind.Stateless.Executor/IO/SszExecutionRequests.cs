// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct SszExecutionRequests
{
    [SszField(0)]
    [SszProgressiveList]
    public DepositRequest[] Deposits { get; set; }

    [SszField(1)]
    [SszProgressiveList]
    public WithdrawalRequest[] Withdrawals { get; set; }

    [SszField(2)]
    [SszProgressiveList]
    public ConsolidationRequest[] Consolidations { get; set; }

    [SszField(3)]
    [SszProgressiveList]
    public BuilderDepositRequest[] BuilderDeposits { get; set; }

    [SszField(4)]
    [SszProgressiveList]
    public BuilderExitRequest[] BuilderExits { get; set; }
}
