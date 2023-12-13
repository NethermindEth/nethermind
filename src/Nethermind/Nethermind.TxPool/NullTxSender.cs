// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    public class NullTxSender : ITxSender
    {
        public static ITxSender Instance { get; } = new NullTxSender();

        public ValueTask<(Hash256, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
            => new((tx.Hash!, null));

    }
}
