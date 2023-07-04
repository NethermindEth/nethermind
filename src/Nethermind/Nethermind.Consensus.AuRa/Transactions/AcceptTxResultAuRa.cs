// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public readonly struct AcceptTxResultAuRa
    {
        /// <summary>
        /// Permission denied for this tx type.
        /// </summary>
        public static readonly AcceptTxResult PermissionDenied = new(100, nameof(PermissionDenied));
    }
}
