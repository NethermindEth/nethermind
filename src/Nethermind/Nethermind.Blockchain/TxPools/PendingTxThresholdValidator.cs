/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TxPools
{
    public class PendingTxThresholdValidator : IPendingTxThresholdValidator
    {
        private readonly int _obsoletePendingTransactionInterval;
        private readonly int _removePendingTransactionInterval;

        public PendingTxThresholdValidator(ITxPoolConfig txPoolConfig)
        {
            if(txPoolConfig == null) throw new ArgumentNullException(nameof(txPoolConfig));
            
            _obsoletePendingTransactionInterval = txPoolConfig.ObsoletePendingTransactionInterval;
            _removePendingTransactionInterval = txPoolConfig.RemovePendingTransactionInterval;
        }

        public bool IsObsolete(UInt256 currentTimestamp, UInt256 transactionTimestamp)
            => !IsTimeInRange(currentTimestamp, transactionTimestamp, _obsoletePendingTransactionInterval);

        public bool IsRemovable(UInt256 currentTimestamp, UInt256 transactionTimestamp)
            => !IsTimeInRange(currentTimestamp, transactionTimestamp, _removePendingTransactionInterval);

        private static bool IsTimeInRange(UInt256 currentTimestamp, UInt256 transactionTimestamp, int threshold)
            => (currentTimestamp - transactionTimestamp) <= threshold;
    }
}