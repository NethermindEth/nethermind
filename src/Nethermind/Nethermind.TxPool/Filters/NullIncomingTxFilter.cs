// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool.Filters
{
    public sealed class NullIncomingTxFilter : IIncomingTxFilter
    {
        private NullIncomingTxFilter() { }

        public static IIncomingTxFilter Instance { get; } = new NullIncomingTxFilter();

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            return AcceptTxResult.Accepted;
        }
    }
}
