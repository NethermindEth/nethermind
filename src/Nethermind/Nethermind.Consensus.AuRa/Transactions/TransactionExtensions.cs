// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public static class TransactionExtensions
    {
        public static bool IsZeroGasPrice(this Transaction tx, BlockHeader parentHeader, ISpecProvider specProvider)
        {
            bool isEip1559Enabled = specProvider.GetSpecFor1559(parentHeader.Number + 1).IsEip1559Enabled;
            bool checkByFeeCap = isEip1559Enabled && tx.Supports1559Fields;
            if (checkByFeeCap && !tx.MaxFeePerGas.IsZero) // only 0 gas price transactions are system transactions and can be whitelisted
            {
                return false;
            }
            else if (!tx.GasPrice.IsZero && !checkByFeeCap)
            {
                return false;
            }

            return true;
        }
    }
}
