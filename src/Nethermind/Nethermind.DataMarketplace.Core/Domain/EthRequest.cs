using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class EthRequest
    {
        public Keccak Id { get; private set; }
        public string Host { get; private set; }
        public Address Address { get; private set; }
        public UInt256 Value { get; private set; }
        public DateTime RequestedAt { get; private set; }
        public Keccak TransactionHash { get; }

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

        public void UpdateRequestDate(DateTime requestedAt)
        {
            RequestedAt = requestedAt;
        }
    }
}