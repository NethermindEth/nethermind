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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.AuRa.Contracts
{
    public class SystemContract
    {
        private readonly Address _contractAddress;

        public SystemContract(Address contractAddress)
        {
            _contractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
        }
        
        protected Transaction GenerateTransaction(byte[] transactionData, long gasLimit = long.MaxValue, UInt256? nonce = null)
        {
            var transaction = new Transaction(true)
            {
                Value = UInt256.Zero,
                Data = transactionData,
                To = _contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = gasLimit,
                GasPrice = UInt256.Zero,
                Nonce = nonce ?? UInt256.Zero,
            };
                
            transaction.Hash = Transaction.CalculateHash(transaction);

            return transaction;
        }
        
        public void InvokeTransaction(BlockHeader header, ITransactionProcessor transactionProcessor, Transaction transaction, CallOutputTracer tracer)
        {
            if (transaction != null)
            {
                transactionProcessor.Execute(transaction, header, tracer);
                
                if (tracer.StatusCode != StatusCode.Success)
                {
                    throw new AuRaException($"System call returned error '{tracer.Error}' at block {header.Number}.");
                }
            }
        }
    }
}