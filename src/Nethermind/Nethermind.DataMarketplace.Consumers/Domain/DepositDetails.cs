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
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Domain
{
    public class DepositDetails : IEquatable<DepositDetails>
    {
        public Keccak Id { get; private set; }
        public Deposit Deposit { get; private set; }
        public DataHeader DataHeader { get; private set; }
        public byte[] Pepper { get; private set; }
        public uint Timestamp { get; private set; }
        public Keccak TransactionHash { get; private set; }
        public uint VerificationTimestamp { get; private set; }
        public bool Verified => VerificationTimestamp > 0;
        public EarlyRefundTicket EarlyRefundTicket { get; private set; }
        public Keccak ClaimedRefundTransactionHash { get; private set; }
        public bool RefundClaimed => !(ClaimedRefundTransactionHash is null);
        public uint ConsumedUnits { get; private set; }
        public string Kyc { get; private set; }

        public DepositDetails(Deposit deposit, DataHeader dataHeader, byte[] pepper, uint timestamp, 
            Keccak transactionHash, uint verificationTimestamp = 0, EarlyRefundTicket earlyRefundTicket = null,
            Keccak claimedRefundTransactionHash = null, string kyc = null)
        {
            Id = deposit.Id;
            Deposit = deposit;
            DataHeader = dataHeader;
            Pepper = pepper;
            Timestamp = timestamp;
            TransactionHash = transactionHash;
            VerificationTimestamp = verificationTimestamp;
            EarlyRefundTicket = earlyRefundTicket;
            SetRefundClaimed(claimedRefundTransactionHash);
            Kyc = kyc;
        }

        public void Verify(uint timestamp)
        {
            VerificationTimestamp = timestamp;
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
               VerificationTimestamp + depositUnits + DataHeader.Rules.Expiry.Value <=
               currentBlockTimestamp;

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