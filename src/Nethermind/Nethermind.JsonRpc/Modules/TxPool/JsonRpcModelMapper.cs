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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    [Todo(Improve.Refactor, "Allow separate mappers to be registered the same way as modules")]
    public class JsonRpcModelMapper : IJsonRpcModelMapper
    {
        public TransactionPoolStatus MapTransactionPoolStatus(TransactionPoolInfo transactionPoolInfo)
        {
            if (transactionPoolInfo == null)
            {
                return null;
            }

            return new TransactionPoolStatus
            {
                Pending = transactionPoolInfo.Pending.Sum(t => t.Value.Count),
                Queued = transactionPoolInfo.Queued.Sum(t => t.Value.Count)
            };
        }

        public TransactionPoolContent MapTransactionPoolContent(TransactionPoolInfo transactionPoolInfo)
        {
            if (transactionPoolInfo == null)
            {
                return null;
            }
            
            var transactionPoolInfoData = MapTransactionPoolInfoData(transactionPoolInfo);

            return new TransactionPoolContent
            {
                Pending = transactionPoolInfoData.Pending,
                Queued = transactionPoolInfoData.Queued
            };
        }

        public TransactionPoolInspection MapTransactionPoolInspection(TransactionPoolInfo transactionPoolInfo)
        {
            if (transactionPoolInfo == null)
            {
                return null;
            }

            var transactionPoolInfoData = MapTransactionPoolInfoData(transactionPoolInfo);

            return new TransactionPoolInspection
            {
                Pending = transactionPoolInfoData.Pending,
                Queued = transactionPoolInfoData.Queued
            };
        }

        private static (IDictionary<Data.Data, Dictionary<Quantity, TransactionForRpc[]>> Pending,
            IDictionary<Data.Data, Dictionary<Quantity, TransactionForRpc[]>> Queued)
            MapTransactionPoolInfoData(TransactionPoolInfo transactionPoolInfo)
            => (transactionPoolInfo.Pending
                    .ToDictionary(k => new Data.Data(k.Key), k => k.Value
                        .ToDictionary(v => new Quantity(v.Key),
                            v => v.Value.Select(t => new TransactionForRpc(null, null, null, t)).ToArray())),
                transactionPoolInfo.Queued
                    .ToDictionary(k => new Data.Data(k.Key), k => k.Value
                        .ToDictionary(v => new Quantity(v.Key),
                            v => v.Value.Select(t => new TransactionForRpc(null, null, null, t)).ToArray())));
    }
}