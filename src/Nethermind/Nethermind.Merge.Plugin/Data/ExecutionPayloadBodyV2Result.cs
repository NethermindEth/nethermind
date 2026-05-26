// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

public class ExecutionPayloadBodyV2Result
{
    private Transaction[]? _sourceTransactions;
    private byte[][]? _transactions;

    public ExecutionPayloadBodyV2Result(Transaction[] transactions, Withdrawal[]? withdrawals, byte[]? blockAccessList)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        _sourceTransactions = transactions;
        Withdrawals = withdrawals;
        BlockAccessList = blockAccessList;
    }

    internal byte[][] EncodedTransactions => _transactions ??= PayloadBodiesDirectResponseWriter.EncodeTransactions(_sourceTransactions!);

    public byte[][] Transactions
    {
        get => EncodedTransactions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _transactions = value;
            _sourceTransactions = null;
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Withdrawal[]? Withdrawals { get; set; }

    public byte[]? BlockAccessList { get; set; }
}
