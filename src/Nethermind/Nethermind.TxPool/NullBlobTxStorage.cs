// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public class NullBlobTxStorage : IBlobTxStorage
{
    public static NullBlobTxStorage Instance { get; } = new();

    public bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out MempoolTransaction? transaction)
    {
        transaction = default;
        return false;
    }

    public int TryGetMany(TxLookupKey[] keys, int count, MempoolTransaction?[] results) => 0;

    public IEnumerable<MempoolTransaction> GetAll() => Array.Empty<MempoolTransaction>();

    public void Add(MempoolTransaction transaction) { }

    public void Delete(in ValueHash256 hash, in UInt256 timestamp) { }

    public bool TryGetBlobTransactionsFromBlock(ulong blockNumber, [NotNullWhen(true)] out MempoolTransaction[]? blockBlobTransactions)
    {
        blockBlobTransactions = default;
        return false;
    }

    public void AddBlobTransactionsFromBlock(ulong blockNumber, in ArrayPoolListRef<MempoolTransaction> blockBlobTransactions) { }

    public void DeleteBlobTransactionsFromBlock(ulong blockNumber) { }
}
