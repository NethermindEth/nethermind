// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public static class DepositTxExtensions
{

    public static bool IsDeposit(this Transaction tx)
    {
        return tx.Type == TxType.DepositTx;
    }
}
