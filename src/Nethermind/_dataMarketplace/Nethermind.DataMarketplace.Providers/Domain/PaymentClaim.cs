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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class PaymentClaim
    {
        private ISet<TransactionInfo> _transactions = new HashSet<TransactionInfo>();
        public Keccak Id { get; private set; }
        public Keccak DepositId { get; private set; }
        public Keccak AssetId { get; private set; }
        public string AssetName { get; private set; }
        public uint Units { get; private set; }
        public uint ClaimedUnits { get; private set; }
        public UnitsRange UnitsRange { get; private set; }
        public UInt256 Value { get; private set; }
        public UInt256 ClaimedValue { get; private set; }
        public uint ExpiryTime { get; private set; }
        public byte[] Pepper { get; private set; }
        public Address Provider { get; private set; }
        public Address Consumer { get; private set; }
        public Signature Signature { get; private set; }
        public TransactionInfo? Transaction { get; private set; }
        public IEnumerable<TransactionInfo> Transactions
        {
            get => _transactions;
            private set => _transactions = new HashSet<TransactionInfo>(value);
        }
        public UInt256 TransactionCost { get; private set; }
        public UInt256 Income { get; private set; }
        public ulong Timestamp { get; private set; }
        public PaymentClaimStatus Status { get; private set; }

        public PaymentClaim(Keccak id, Keccak depositId, Keccak assetId, string assetName, uint units,
            uint claimedUnits, UnitsRange unitsRange, UInt256 value, UInt256 claimedValue, uint expiryTime,
            byte[] pepper, Address provider, Address consumer, Signature signature, ulong timestamp,
            IEnumerable<TransactionInfo> transactions, PaymentClaimStatus status)
        {
            Id = id;
            DepositId = depositId;
            AssetId = assetId;
            AssetName = assetName;
            Units = units;
            ClaimedUnits = claimedUnits;
            UnitsRange = unitsRange;
            Value = value;
            ClaimedValue = claimedValue;
            ExpiryTime = expiryTime;
            Pepper = pepper;
            Provider = provider;
            Consumer = consumer;
            Signature = signature;
            Timestamp = timestamp;
            Transactions = transactions ?? Enumerable.Empty<TransactionInfo>();
            Status = status;
            
            if (Transactions.Any())
            {
                Transaction = Transactions.SingleOrDefault(t => t.State == TransactionState.Included) ??
                              Transactions.OrderBy(t => t.Timestamp).LastOrDefault();
            }
        }

        public void AddTransaction(TransactionInfo transaction)
        {
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            _transactions.Add(transaction);
        }
        
        public void SetIncludedTransaction(Keccak transactionHash)
        {
            Transaction = _transactions.Single(t => t.Hash == transactionHash);
            Transaction.SetIncluded();
            foreach (var transaction in _transactions)
            {
                if (Transaction.Equals(transaction))
                {
                    continue;
                }
                
                transaction.SetRejected();
            }

            if (Transaction.Type == TransactionType.Cancellation)
            {
                Status = PaymentClaimStatus.Cancelled;
            }
        }


        public void SetStatus(PaymentClaimStatus status)
        {
            Status = status;
        }

        public void SetTransactionCost(UInt256 transactionCost)
        {
            TransactionCost = transactionCost;
            if (TransactionCost <= ClaimedValue)
            {
                Income = ClaimedValue - TransactionCost;
                Status = PaymentClaimStatus.Claimed;
            }
            else
            {
                Income = 0;
                Status = PaymentClaimStatus.ClaimedWithLoss;
            }
        }
        
        public void Reject()
        {
            Status = PaymentClaimStatus.Rejected;
        }
    }
}