// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV1Result(IReadOnlyList<Transaction> transactions, IReadOnlyList<Withdrawal>? withdrawals)
{
    public IReadOnlyList<byte[]> Transactions { get; set; } = PayloadBodiesDirectResponseWriter.EncodeTransactions(transactions);

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<Withdrawal>? Withdrawals { get; set; } = withdrawals;
}
