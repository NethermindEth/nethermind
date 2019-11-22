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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositDetailsForRpc
    {
        public Keccak Id { get; set; }
        public DepositForRpc Deposit { get; set; }
        public DataAssetForRpc DataAsset { get; set; }
        public Address Consumer { get; set; }
        public uint Timestamp { get; set; }
        public Keccak TransactionHash { get; set; }
        public UInt256 TransactionGasPrice { get; set; }
        public uint ConfirmationTimestamp { get; set; }
        public bool Confirmed { get; set; }
        public bool Rejected { get; set; }
        public bool Expired { get; set; }
        public bool RefundClaimed { get; set; }
        public Keccak ClaimedRefundTransactionHash { get; set; }
        public UInt256 ClaimedRefundTransactionGasPrice { get; set; }
        public uint ConsumedUnits { get; set; }
        public string Kyc { get; set; }
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
            TransactionHash = deposit.TransactionHash;
            TransactionGasPrice = deposit.TransactionGasPrice;
            ConfirmationTimestamp = deposit.ConfirmationTimestamp;
            Confirmed = deposit.Confirmed;
            Rejected = deposit.Rejected;
            Expired = deposit.IsExpired(timestamp);
            RefundClaimed = deposit.RefundClaimed;
            ClaimedRefundTransactionHash = deposit.ClaimedRefundTransactionHash;
            ClaimedRefundTransactionGasPrice = deposit.ClaimedRefundTransactionGasPrice;
            ConsumedUnits = deposit.ConsumedUnits;
            Kyc = deposit.Kyc;
            Confirmations = deposit.Confirmations;
            RequiredConfirmations = deposit.RequiredConfirmations;
        }
    }
}