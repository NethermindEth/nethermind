// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class FaucetRequestDetailsForRpc
    {
        public string? Host { get; set; }
        public Address? Address { get; set; }
        public UInt256? Value { get; set; }
        public DateTime? Date { get; set; }
        public Keccak? TransactionHash { get; set; }

        public FaucetRequestDetailsForRpc()
        {
        }

        public FaucetRequestDetailsForRpc(FaucetRequestDetails request)
            : this(request.Host, request.Address, request.Value, request.Date, request.TransactionHash)
        {
        }

        public FaucetRequestDetailsForRpc(string? host, Address? address, UInt256? value, DateTime? date, Keccak? transactionHash)
        {
            Host = host;
            Address = address;
            Value = value;
            Date = date;
            TransactionHash = transactionHash;
        }
    }
}
