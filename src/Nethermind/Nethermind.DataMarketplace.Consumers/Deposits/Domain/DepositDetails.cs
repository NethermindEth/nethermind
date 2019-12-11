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
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Domain
{
    public class DepositDetails : IEquatable<DepositDetails>
    {
        public Keccak Id { get; private set; }
        public Deposit Deposit { get; private set; }
        public DataAsset DataAsset { get; private set; }
        public Address Consumer { get; private set; }
        public byte[] Pepper { get; private set; }
        public uint Timestamp { get; private set; }
        public TransactionInfo Transaction { get; private set; }
        public uint ConfirmationTimestamp { get; private set; }
        public bool Confirmed => ConfirmationTimestamp > 0 && Confirmations >= RequiredConfirmations;
        public bool Rejected { get; private set; }
        public EarlyRefundTicket EarlyRefundTicket { get; private set; }
        public TransactionInfo ClaimedRefundTransaction { get; private set; }
        public bool RefundClaimed { get; private set; }
        public uint ConsumedUnits { get; private set; }
        public string Kyc { get; private set; }
        public uint Confirmations { get; private set; }
        public uint RequiredConfirmations { get; private set; }

        public DepositDetails(Deposit deposit, DataAsset dataAsset, Address consumer, byte[] pepper, uint timestamp,
            TransactionInfo transaction, uint confirmationTimestamp = 0, bool rejected = false,
            EarlyRefundTicket earlyRefundTicket = null, TransactionInfo claimedRefundTransaction = null,
            bool refundClaimed = false, string kyc = null, uint confirmations = 0, uint requiredConfirmations = 0)
        {
            Id = deposit.Id;
            Deposit = deposit;
            DataAsset = dataAsset;
            Consumer = consumer;
            Pepper = pepper;
            Timestamp = timestamp;
            Transaction = transaction;
            ConfirmationTimestamp = confirmationTimestamp;
            Rejected = rejected;
            EarlyRefundTicket = earlyRefundTicket;
            ClaimedRefundTransaction = claimedRefundTransaction;
            RefundClaimed = refundClaimed;
            Kyc = kyc;
            Confirmations = confirmations;
            RequiredConfirmations = requiredConfirmations;
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

        public void SetTransaction(TransactionInfo transaction)
        {
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        }
        
        public void Reject()
        {
            Rejected = true;
        }

        public void SetEarlyRefundTicket(EarlyRefundTicket earlyRefundTicket)
        {
            EarlyRefundTicket = earlyRefundTicket;
        }

        public void SetClaimedRefundTransaction(TransactionInfo transaction)
        {
            ClaimedRefundTransaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        }

        public void SetRefundClaimed()
        {
            RefundClaimed = true;
        }
        
        public void SetConsumedUnits(uint units)
        {
            ConsumedUnits = units;
        }

        public bool CanClaimEarlyRefund(ulong currentBlockTimestamp)
            => Confirmed && !Rejected && !RefundClaimed && !(EarlyRefundTicket is null) &&
               ClaimedRefundTransaction?.State != TransactionState.Canceled &&
               EarlyRefundTicket.ClaimableAfter <= currentBlockTimestamp;

        public bool CanClaimRefund(ulong currentBlockTimestamp)
            => Confirmed && !Rejected && !RefundClaimed &&
               ClaimedRefundTransaction?.State != TransactionState.Canceled &&
               currentBlockTimestamp >= Deposit.ExpiryTime &&
               ConfirmationTimestamp + Deposit.Units + DataAsset.Rules.Expiry.Value <= currentBlockTimestamp;

        public void SetConfirmations(uint confirmations)
        {
            Confirmations = confirmations;
        }

        public bool Equals(DepositDetails other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Id, other.Id);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((DepositDetails) obj);
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