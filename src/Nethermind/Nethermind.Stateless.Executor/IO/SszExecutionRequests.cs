// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct SszExecutionRequests
{
    [SszList(0x2000)]
    public DepositRequest[] Deposits { get; set; }

    [SszList(0x10)]
    public WithdrawalRequest[] Withdrawals { get; set; }

    [SszList(0x2)]
    public ConsolidationRequest[] Consolidations { get; set; }

    [SszList(0x40)]
    public BuilderDepositRequest[] BuilderDeposits { get; set; }

    [SszList(0x10)]
    public BuilderExitRequest[] BuilderExits { get; set; }
}
