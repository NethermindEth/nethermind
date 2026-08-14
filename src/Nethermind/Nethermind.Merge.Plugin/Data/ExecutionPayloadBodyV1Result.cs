// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV1Result(Transaction[] transactions, Withdrawal[]? withdrawals)
{
    private Transaction[]? _sourceTransactions = transactions;
    private byte[][]? _transactions;

    internal Transaction[]? SourceTransactions => _sourceTransactions;

    internal byte[][] EncodedTransactions => _transactions ??= PayloadBodiesDirectResponseWriter.EncodeTransactions(_sourceTransactions!);

    public byte[][] Transactions
    {
        get => EncodedTransactions;
        set
        {
            _transactions = value;
            _sourceTransactions = null;
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Withdrawal[]? Withdrawals { get; set; } = withdrawals;
}
