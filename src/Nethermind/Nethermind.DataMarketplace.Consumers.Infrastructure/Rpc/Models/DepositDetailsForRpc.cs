// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositDetailsForRpc
    {
        public Keccak? Id { get; set; }
        public DepositForRpc? Deposit { get; set; }
        public DataAssetForRpc? DataAsset { get; set; }
        public Address? Consumer { get; set; }
        public uint Timestamp { get; set; }
        public IEnumerable<TransactionInfoForRpc>? Transactions { get; set; }
        public TransactionInfoForRpc? Transaction { get; set; }
        public uint ConfirmationTimestamp { get; set; }
        public bool Confirmed { get; set; }
        public bool Rejected { get; set; }
        public bool Cancelled { get; private set; }
        public bool Expired { get; set; }
        public bool RefundClaimed { get; set; }
        public bool RefundCancelled { get; private set; }
        public TransactionInfoForRpc? ClaimedRefundTransaction { get; set; }
        public IEnumerable<TransactionInfoForRpc>? ClaimedRefundTransactions { get; set; }
        public uint ConsumedUnits { get; set; }
        public string? Kyc { get; set; }
        public uint Confirmations { get; set; }
        public uint RequiredConfirmations { get; set; }

        public DepositDetailsForRpc()
        {
        }

        public DepositDetailsForRpc(DepositDetails deposit, uint timestamp)
        {
            Id = deposit.Id;
            Deposit = new DepositForRpc(deposit.Deposit);
            DataAsset = new DataAssetForRpc(deposit.DataAsset);
            Consumer = deposit.Consumer;
            Timestamp = deposit.Timestamp;
            Transaction = deposit.Transaction is null ? null : new TransactionInfoForRpc(deposit.Transaction);
            Transactions = deposit.Transactions?.Select(t => new TransactionInfoForRpc(t)).OrderBy(t => t.Timestamp) ??
                           Enumerable.Empty<TransactionInfoForRpc>();
            ConfirmationTimestamp = deposit.ConfirmationTimestamp;
            Confirmed = deposit.Confirmed;
            Rejected = deposit.Rejected;
            Cancelled = deposit.Cancelled;
            Expired = deposit.IsExpired(timestamp);
            RefundClaimed = deposit.RefundClaimed;
            ClaimedRefundTransaction = deposit.ClaimedRefundTransaction is null
                ? null
                : new TransactionInfoForRpc(deposit.ClaimedRefundTransaction);
            ClaimedRefundTransactions = deposit.ClaimedRefundTransactions?.Select(t => new TransactionInfoForRpc(t))
                                            .OrderBy(t => t.Timestamp) ??
                                        Enumerable.Empty<TransactionInfoForRpc>();
            RefundCancelled = deposit.RefundCancelled;
            ConsumedUnits = deposit.ConsumedUnits;
            Kyc = deposit.Kyc;
            Confirmations = deposit.Confirmations;
            RequiredConfirmations = deposit.RequiredConfirmations;
        }
    }
}
