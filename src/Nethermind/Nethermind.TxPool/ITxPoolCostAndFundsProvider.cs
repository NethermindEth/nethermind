// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface ITxPoolCostAndFundsProvider
{
    UInt256 GetAdditionalFunds(Transaction tx);
    bool TryGetTransactionCost(Transaction tx, out UInt256 txCost);
}

public sealed class DefaultTxPoolCostAndFundsProvider : ITxPoolCostAndFundsProvider
{
    public static ITxPoolCostAndFundsProvider Instance { get; } = new DefaultTxPoolCostAndFundsProvider();

    private DefaultTxPoolCostAndFundsProvider() { }

    public UInt256 GetAdditionalFunds(Transaction tx) => UInt256.Zero;

    public bool TryGetTransactionCost(Transaction tx, out UInt256 txCost)
        => !tx.IsOverflowWhenAddingTxCostToCumulative(UInt256.Zero, out txCost);
}
