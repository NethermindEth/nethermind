// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV2Result : ExecutionPayloadBodyV1Result
{
    public ExecutionPayloadBodyV2Result(
        IReadOnlyList<Transaction> transactions,
        IReadOnlyList<Withdrawal>? withdrawals,
        IReadOnlyList<Deposit>? deposits,
        IReadOnlyList<WithdrawalRequest>? withdrawalsRequests,
        IReadOnlyList<ConsolidationRequest>? consolidationRequests
    )
        : base(transactions, withdrawals)
    {
        DepositRequests = deposits;
        WithdrawalRequests = withdrawalsRequests;
        ConsolidationRequests = consolidationRequests;
    }

    /// <summary>
    /// Deposit requests <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/prague.md#specification-2"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<Deposit>? DepositRequests { get; set; }

    /// <summary>
    /// Withdrawal requests <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/prague.md#specification-2"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<WithdrawalRequest>? WithdrawalRequests { get; set; }

    /// <summary>
    /// Consolidation requests <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/prague.md#specification-2"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<ConsolidationRequest>? ConsolidationRequests { get; set; }
}
