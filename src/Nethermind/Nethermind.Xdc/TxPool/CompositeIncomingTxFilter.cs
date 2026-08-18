// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Xdc.TxPool;

/// <summary>
/// Runs the given filters in order, returning the first non-accepting result.
/// </summary>
/// <remarks>
/// The pool takes a single custom incoming filter, so XDC-specific filters are combined here.
/// </remarks>
internal sealed class CompositeIncomingTxFilter(params IIncomingTxFilter[] filters) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        foreach (IIncomingTxFilter filter in filters)
        {
            AcceptTxResult result = filter.Accept(tx, ref state, txHandlingOptions);
            if (!result)
                return result;
        }

        return AcceptTxResult.Accepted;
    }
}
