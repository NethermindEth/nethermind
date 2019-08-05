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

namespace Nethermind.DataMarketplace.Consumers.Domain
{
    public class DepositDetails : IEquatable<DepositDetails>
    {
        public Keccak Id { get; private set; }
        public Deposit Deposit { get; private set; }
        public DataHeader DataHeader { get; private set; }
        public Address Consumer { get; private set; }
        public byte[] Pepper { get; private set; }
        public uint Timestamp { get; private set; }
        public Keccak TransactionHash { get; private set; }
        public uint ConfirmationTimestamp { get; private set; }
        public bool Confirmed => ConfirmationTimestamp > 0 && Confirmations >= RequiredConfirmations;
        public EarlyRefundTicket EarlyRefundTicket { get; private set; }
        public Keccak ClaimedRefundTransactionHash { get; private set; }
        public bool RefundClaimed => !(ClaimedRefundTransactionHash is null);
        public uint ConsumedUnits { get; private set; }
        public string Kyc { get; private set; }
        public uint Confirmations { get; private set; }
        public uint RequiredConfirmations { get; private set; }

        public DepositDetails(Deposit deposit, DataHeader dataHeader, Address consumer, byte[] pepper, uint timestamp, 
            Keccak transactionHash, uint confirmationTimestamp = 0, EarlyRefundTicket earlyRefundTicket = null,
            Keccak claimedRefundTransactionHash = null, string kyc = null, uint confirmations = 0,
            uint requiredConfirmations = 0)
        {
            Id = deposit.Id;
            Deposit = deposit;
            DataHeader = dataHeader;
            Consumer = consumer;
            Pepper = pepper;
            Timestamp = timestamp;
            TransactionHash = transactionHash;
            SetConfirmationTimestamp(confirmationTimestamp);
            EarlyRefundTicket = earlyRefundTicket;
            SetRefundClaimed(claimedRefundTransactionHash);
            Kyc = kyc;
            Confirmations = confirmations;
            RequiredConfirmations = requiredConfirmations;
        }

        public void SetConfirmationTimestamp(uint timestamp)
        {
            ConfirmationTimestamp = timestamp;
        }

        public void SetEarlyRefundTicket(EarlyRefundTicket earlyRefundTicket)
        {
            EarlyRefundTicket = earlyRefundTicket;
        }

        public void SetRefundClaimed(Keccak transactionHash)
        {
            ClaimedRefundTransactionHash = transactionHash;
        }
        
        public void SetConsumedUnits(uint units)
        {
            ConsumedUnits = units;
        }

        public bool CanClaimEarlyRefund(ulong currentBlockTimestamp)
            => !RefundClaimed && !(EarlyRefundTicket is null) &&
               EarlyRefundTicket.ClaimableAfter <= currentBlockTimestamp;

        public bool CanClaimRefund(ulong currentBlockTimestamp, uint depositUnits)
            => !RefundClaimed && currentBlockTimestamp >= Deposit.ExpiryTime &&
               ConfirmationTimestamp + depositUnits + DataHeader.Rules.Expiry.Value <=
               currentBlockTimestamp;

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