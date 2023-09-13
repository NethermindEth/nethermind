// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

public class NullBlobTxStorage : ITxStorage
{
    public static NullBlobTxStorage Instance { get; } = new();

    public bool TryGet(ValueKeccak hash, [NotNullWhen(true)] out Transaction? transaction)
    {
        transaction = default;
        return false;
    }

    public IEnumerable<Transaction> GetAll()
    {
        return ArraySegment<Transaction>.Empty;
    }

    public void Add(Transaction transaction) { }

    public void Delete(ValueKeccak hash) { }
}
