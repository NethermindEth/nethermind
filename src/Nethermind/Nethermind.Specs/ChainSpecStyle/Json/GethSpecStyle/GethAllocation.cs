#nullable enable

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Specs.GethSpecStyle
{
    public class GethAllocation
    {
        public GethAllocation() { }
        public GethAllocation(UInt256 allocationBalance) {
            Balance = allocationBalance;
        }
        public GethAllocation(byte[]? code, Dictionary<UInt256, byte[]>? storage, UInt256? balance, UInt256? nonce, byte[]? privateKey)
        {
            Code = code;
            Storage = storage;
            Balance = balance;
            Nonce = nonce;
            PrivateKey = privateKey;
        }
        public byte[]? Code { get; set; }
        public Dictionary<UInt256, byte[]>? Storage { get; set; }
        public UInt256? Balance { get; set; }
        public UInt256? Nonce { get; set; }
        public byte[]? PrivateKey { get; set; }

        public static implicit operator ChainSpecAllocation(GethAllocation gethAlloc) => new ChainSpecAllocation
        {
            Balance = gethAlloc.Balance ?? 0,
            Nonce = gethAlloc.Nonce ?? 0,
            Storage = gethAlloc.Storage ?? new Dictionary<UInt256, byte[]>(),
            Constructor = gethAlloc.PrivateKey,
            Code = gethAlloc.Code
        };
    }
}
