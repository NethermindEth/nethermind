// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Data;

/// <summary>A block's receipts rented from the array pool.</summary>
/// <remarks>Owns the receipts: disposing returns the list and every receipt's pooled logs.</remarks>
public sealed class ReceiptsForRpc<TReceipt>(int capacity) : IReadOnlyList<TReceipt>, IDisposable
    where TReceipt : ReceiptForRpc
{
    private readonly ArrayPoolList<TReceipt> _receipts = new(capacity);

    public int Count => _receipts.Count;

    public TReceipt this[int index] => _receipts[index];

    public void Add(TReceipt receipt) => _receipts.Add(receipt);

    public IEnumerator<TReceipt> GetEnumerator() => _receipts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _receipts.DisposeRecursive();
}
