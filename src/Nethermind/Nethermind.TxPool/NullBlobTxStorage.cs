// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public class NullBlobTxStorage : IBlobTxStorage
{
    public static NullBlobTxStorage Instance { get; } = new();

    public bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction)
    {
        transaction = default;
        return false;
    }

    public IEnumerable<LightTransaction> GetAll() => Array.Empty<LightTransaction>();

    public void Add(Transaction transaction) { }

    public void Delete(in ValueHash256 hash, in UInt256 timestamp) { }

    public bool TryGetBlobTransactionsFromBlock(long blockNumber, out Transaction[]? blockBlobTransactions)
    {
        blockBlobTransactions = default;
        return false;
    }

    public void AddBlobTransactionsFromBlock(long blockNumber, IList<Transaction> blockBlobTransactions) { }

    public void DeleteBlobTransactionsFromBlock(long blockNumber) { }
}
