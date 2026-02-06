// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;
using System;

namespace Nethermind.Xdc.TxPool;

internal sealed class SignTransactionFilter(ISigner signer, ISpecProvider specProvider) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        var spec = (IXdcReleaseSpec)specProvider.GetFinalSpec();
        if (tx.IsSignTransaction(spec) && tx.SenderAddress != signer.Address)
        {
            return AcceptTxResult.Invalid;
        }
        return AcceptTxResult.Accepted;
    }
}
