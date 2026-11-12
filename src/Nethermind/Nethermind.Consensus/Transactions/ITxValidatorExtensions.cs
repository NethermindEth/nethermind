// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions;

public static class ITxValidatorExtensions
{
    public static ITxFilter AsTxFilter(this ITxValidator txValidator) => new TxValidatorTxFilter(txValidator);

    private class TxValidatorTxFilter(ITxValidator headTxValidator) : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context)
            => headTxValidator.IsWellFormed(tx, context.Spec) ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
    }
}

