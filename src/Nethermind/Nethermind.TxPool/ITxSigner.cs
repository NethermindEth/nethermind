// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public interface ITxSigner : ITxSealer
    {
        ValueTask Sign(Transaction tx);

        ValueTask ITxSealer.Seal(Transaction tx, TxHandlingOptions txHandlingOptions) => Sign(tx);
    }
}
