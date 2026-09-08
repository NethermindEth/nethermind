// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public interface ITxSigner : ITxSealer
    {
        bool TrySign(Transaction tx);

        ValueTask Sign(Transaction tx)
        {
            if (!TrySign(tx))
                throw new InvalidOperationException($"Signer could not sign transaction for {tx.SenderAddress}.");
            return default;
        }

        bool ITxSealer.TrySeal(Transaction tx, TxHandlingOptions txHandlingOptions) => TrySign(tx);
    }
}
