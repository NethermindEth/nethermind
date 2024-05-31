// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV2Result: ExecutionPayloadBodyV1Result
{
    public ExecutionPayloadBodyV2Result(
        IList<Transaction> transactions,
        IList<Withdrawal>? withdrawals,
        IList<Deposit>? deposits,
        IList<WithdrawalRequest>? withdrawalsRequests
    )
        : base(transactions, withdrawals)
    {
        DepositRequests = deposits;
        WithdrawalRequests = withdrawalsRequests;
    }

    /// <summary>
    /// Deposit requests <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/prague.md#specification-2"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IList<Deposit>? DepositRequests { get; set; }

    /// <summary>
    /// Withdrawal requests <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/prague.md#specification-2"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IList<WithdrawalRequest>? WithdrawalRequests { get; set; }
}