// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class EthRequest
    {
        public Keccak Id { get; private set; }
        public string Host { get; private set; }
        public Address Address { get; private set; }
        public UInt256 Value { get; private set; }
        public DateTime RequestedAt { get; private set; }
        public Keccak TransactionHash { get; private set; }

        public EthRequest(Keccak id, string host, Address address, UInt256 value, DateTime requestedAt,
            Keccak transactionHash)
        {
            Id = id;
            Host = host;
            Address = address;
            Value = value;
            RequestedAt = requestedAt;
            TransactionHash = transactionHash;
        }

        public void UpdateRequestDetails(Address address, UInt256 value, DateTime requestedAt, Keccak transactionHash)
        {
            Address = address;
            Value = value;
            RequestedAt = requestedAt;
            TransactionHash = transactionHash;
        }
    }
}
