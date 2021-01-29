//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Domain
{
    public class DepositDetails : IEquatable<DepositDetails>
    {
        private ISet<TransactionInfo> _transactions = new HashSet<TransactionInfo>();
        private ISet<TransactionInfo> _claimedRefundTransactions = new HashSet<TransactionInfo>();
        public Keccak Id { get; private set; }
        public Deposit Deposit { get; private set; }
        public DataAsset DataAsset { get; private set; }
        public Address Consumer { get; private set; }
        public byte[] Pepper { get; private set; }
        public uint Timestamp { get; private set; }

        public IEnumerable<TransactionInfo> Transactions
        {
            get => _transactions;
            private set => _transactions = new HashSet<TransactionInfo>(value ?? new List<TransactionInfo>());
        }

        public TransactionInfo? Transaction { get; private set; }
        public uint ConfirmationTimestamp { get; private set; }
        public bool Confirmed => ConfirmationTimestamp > 0 && Confirmations >= RequiredConfirmations;
        public bool Rejected { get; private set; }
        public bool Cancelled { get; private set; }
        public EarlyRefundTicket? EarlyRefundTicket { get; private set; }

        public IEnumerable<TransactionInfo> ClaimedRefundTransactions
        {
            get => _claimedRefundTransactions;
            private set => _claimedRefundTransactions = new HashSet<TransactionInfo>(value);
        }
        
        public TransactionInfo? ClaimedRefundTransaction { get; private set; }
        public bool RefundCancelled { get; private set; }
        public bool RefundClaimed { get; private set; }

        // Consumed units are set by DepositUnitsCalculator - not readable from DB
        public uint ConsumedUnits { get; private set; }
        public string? Kyc { get; private set; }
        public uint Confirmations { get; private set; }
        public uint RequiredConfirmations { get; private set; }

        public DepositDetails(
            Deposit deposit,
            DataAsset dataAsset,
            Address consumer,
            byte[] pepper,
            uint timestamp,
            IEnumerable<TransactionInfo> transactions,
            uint confirmationTimestamp = 0,
            bool rejected = false,
            bool cancelled = false,
            EarlyRefundTicket? earlyRefundTicket = null,
            IEnumerable<TransactionInfo>? claimedRefundTransactions = null,
            bool refundClaimed = false,
            bool refundCancelled = false,
            string? kyc = null,
            uint confirmations = 0,
            uint requiredConfirmations = 0)
        {
            Id = deposit.Id;
            Deposit = deposit;
            DataAsset = dataAsset;
            Consumer = consumer;
            Pepper = pepper;
            Timestamp = timestamp;
            Transactions = transactions;
            ConfirmationTimestamp = confirmationTimestamp;
            Rejected = rejected;
            Cancelled = cancelled;
            EarlyRefundTicket = earlyRefundTicket;
            ClaimedRefundTransactions = claimedRefundTransactions ?? Enumerable.Empty<TransactionInfo>();
            RefundClaimed = refundClaimed;
            RefundCancelled = refundCancelled;
            Kyc = kyc;
            Confirmations = confirmations;
            RequiredConfirmations = requiredConfirmations;

            if (Transactions.Any())
            {
                Transaction = Transactions.SingleOrDefault(t => t.State == TransactionState.Included) ??
                              Transactions.OrderBy(t => t.Timestamp).LastOrDefault();
            }

            if (ClaimedRefundTransactions.Any())
            {
                ClaimedRefundTransaction = ClaimedRefundTransactions.SingleOrDefault(
                                               t => t.State == TransactionState.Included) ??
                                           ClaimedRefundTransactions.OrderBy(t => t.Timestamp).LastOrDefault();
            }
        }

        public bool IsExpired(uint timestamp) => timestamp >= Deposit.ExpiryTime;

        public void SetConfirmationTimestamp(uint timestamp)
        {
            if (timestamp == 0)
            {
                throw new InvalidOperationException($"Confirmation timestamp for deposit with id: '{Id}' cannot be 0.");
            }
            
            ConfirmationTimestamp = timestamp;
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
                Cancelled = true;
            }
        }
        
        public void Reject()
        {
            Rejected = true;
        }

        public void SetEarlyRefundTicket(EarlyRefundTicket earlyRefundTicket)
        {
            EarlyRefundTicket = earlyRefundTicket ?? throw new ArgumentNullException(nameof(earlyRefundTicket));
        }

        public void AddClaimedRefundTransaction(TransactionInfo transaction)
        {
            ClaimedRefundTransaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            _claimedRefundTransactions.Add(transaction);
        }

        public void SetIncludedClaimedRefundTransaction(Keccak transactionHash)
        {
            ClaimedRefundTransaction = _claimedRefundTransactions.Single(t => t.Hash == transactionHash);
            ClaimedRefundTransaction.SetIncluded();
            foreach (var transaction in _claimedRefundTransactions)
            {
                if (ClaimedRefundTransaction.Equals(transaction))
                {
                    continue;
                }

                transaction.SetRejected();
            }
            
            if (ClaimedRefundTransaction.Type == TransactionType.Cancellation)
            {
                RefundCancelled = true;
            }
        }

        public void SetRefundClaimed()
        {
            RefundClaimed = true;
        }
        
        public void SetConsumedUnits(uint units)
        {
            ConsumedUnits = units;
        }

        public bool CanClaimEarlyRefund(ulong currentBlockTimestamp, uint depositTimestamp)
            => Claimable && !(EarlyRefundTicket is null) && (depositTimestamp + EarlyRefundTicket.ClaimableAfter <= currentBlockTimestamp);

        public bool CanClaimRefund(ulong currentBlockTimestamp)
            => Claimable && (currentBlockTimestamp >= Deposit.ExpiryTime);

        public UInt256 GetTimeLeftToClaimRefund(ulong currentBlockTimestamp)
        {
            UInt256 timeLeftToClaimRefund;
            try
            {
                timeLeftToClaimRefund = checked(Deposit.ExpiryTime - currentBlockTimestamp);
            }
            catch(OverflowException)
            {
                timeLeftToClaimRefund = 0;
            }

            if (Claimable && timeLeftToClaimRefund > 0)
            {
                return timeLeftToClaimRefund;
            }

            return 0;
        }

        private bool Claimable => Confirmed && !Rejected && !Cancelled && !RefundClaimed && !RefundCancelled;

        public void SetConfirmations(uint confirmations)
        {
            Confirmations = confirmations;
        }

        public bool Equals(DepositDetails other)
        {
            if (ReferenceEquals(null, other)) return false;
            return ReferenceEquals(this, other) || Equals(Id, other.Id);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == this.GetType() && Equals((DepositDetails) obj);
        }

        public override int GetHashCode()
        {
            return (Id != null ? Id.GetHashCode() : 0);
        }

        public static bool operator ==(DepositDetails left, DepositDetails right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(DepositDetails left, DepositDetails right)
        {
            return !Equals(left, right);
        }
    }
}
