// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions;

public static class ITxValidatorExtensions
{
    public static ITxFilter AsTxFilter(this ITxValidator txValidator) => new TxValidatorTxFilter(txValidator);

    private class TxValidatorTxFilter(ITxValidator headTxValidator) : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader _, IReleaseSpec currentSpec)
            => headTxValidator.IsWellFormed(tx, currentSpec) ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
    }
}

