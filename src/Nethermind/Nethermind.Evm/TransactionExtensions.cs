// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class TransactionExtensions
    {
        public static Address? GetRecipient(this Transaction tx, in UInt256 nonce) =>
            tx.To is not null
                ? tx.To
                : tx.IsSystem()
                    ? tx.SenderAddress
                    : ContractAddress.From(tx.SenderAddress, nonce > 0 ? nonce - 1 : nonce);
    }
}
