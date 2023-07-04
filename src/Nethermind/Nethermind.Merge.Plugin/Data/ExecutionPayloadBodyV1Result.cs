// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV1Result
{
    public ExecutionPayloadBodyV1Result(IList<Transaction> transactions, IList<Withdrawal>? withdrawals)
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

    public IList<IList<byte>> Transactions { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public IList<Withdrawal>? Withdrawals { get; set; }
}
