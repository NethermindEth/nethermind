// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV1Result
{
    public ExecutionPayloadBodyV1Result(IReadOnlyList<Transaction> transactions, IReadOnlyList<Withdrawal>? withdrawals)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var t = new byte[transactions.Count][];

        for (int i = 0, count = t.Length; i < count; i++)
        {
            t[i] = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        Transactions = t;
        Withdrawals = withdrawals;
    }

    public IReadOnlyList<byte[]> Transactions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<Withdrawal>? Withdrawals { get; set; }
}
