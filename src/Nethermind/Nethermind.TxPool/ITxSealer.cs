// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    /// <summary>
    /// Interface for classes that try to make final changes to the transaction object before it is broadcast.
    /// </summary>
    public interface ITxSealer
    {
        bool TrySeal(Transaction tx, TxHandlingOptions txHandlingOptions);

        ValueTask Seal(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            if (!TrySeal(tx, txHandlingOptions))
                throw new InvalidOperationException($"Sealer could not seal transaction for {tx.SenderAddress}.");
            return default;
        }
    }
}
