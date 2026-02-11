// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV2Result
{
    public ExecutionPayloadBodyV2Result(IReadOnlyList<Transaction> transactions, IReadOnlyList<Withdrawal>? withdrawals, byte[]? blockAccessList)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var t = new byte[transactions.Count][];

        for (int i = 0, count = t.Length; i < count; i++)
        {
            t[i] = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        Transactions = t;
        Withdrawals = withdrawals;
        BlockAccessList = blockAccessList;
    }

    public IReadOnlyList<byte[]> Transactions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<Withdrawal>? Withdrawals { get; set; }

    public byte[]? BlockAccessList { get; set; }
}
