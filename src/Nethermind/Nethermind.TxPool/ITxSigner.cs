// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool
{
    public interface ITxSigner : ITxSealer
    {
        bool TrySign(Transaction tx);

        bool ITxSealer.TrySeal(Transaction tx, TxHandlingOptions txHandlingOptions) => TrySign(tx);
    }
}
