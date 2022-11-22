// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.Domain
{
    public class PaymentClaim
    {
        private ISet<TransactionInfo> _transactions = new HashSet<TransactionInfo>();
        public Keccak Id { get; }
        public Keccak DepositId { get; }
        public Keccak AssetId { get; }
        public string AssetName { get; }
        public uint Units { get; }
        public uint ClaimedUnits { get; }
        public UnitsRange UnitsRange { get; }
        public UInt256 Value { get; }
        public UInt256 ClaimedValue { get; }
        public uint ExpiryTime { get; }
        public byte[] Pepper { get; }
        public Address Provider { get; }
        public Address Consumer { get; }
        public Signature Signature { get; }
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
    }
}
