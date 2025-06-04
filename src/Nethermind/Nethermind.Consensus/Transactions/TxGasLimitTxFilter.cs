// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions;

public class TxGasLimitTxFilter : ITxFilter
{
    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec spec)
        => !spec.IsEip7825Enabled || tx.GasLimit <= Eip7825Constants.DefaultTxGasLimitCap ? AcceptTxResult.Accepted : AcceptTxResult.GasLimitExceeded;
}
