// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using System.Collections;

namespace Nethermind.TxPool.Filters;
internal sealed class DelegatedAccountFilter(
        IChainHeadSpecProvider specProvider,
        IDictionary pendingDelegations
        )
    : IIncomingTxFilter
{

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
        if (!spec.IsEip7702Enabled)
            return AcceptTxResult.Accepted;


        return AcceptTxResult.Accepted;
    }
}
