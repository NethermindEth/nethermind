// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    public interface ITxSender
    {
        ValueTask<(Hash256 Hash, AcceptTxResult? AddTxResult)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions);
    }
}
