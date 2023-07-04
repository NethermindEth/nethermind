// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public interface IMinGasPriceTxFilter : ITxFilter
    {
        /*
         * In standard MinGasPriceTxFilter we're basing on value provided from config.
         * The additional method allows us to specify a custom min gas price floor.
         */

        AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, in UInt256 minGasPriceFloor);
    }
}
