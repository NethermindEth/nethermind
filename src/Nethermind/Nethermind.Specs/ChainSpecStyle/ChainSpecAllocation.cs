// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class ChainSpecAllocation
    {
        public ChainSpecAllocation()
        {
        }

        public ChainSpecAllocation(UInt256 allocationValue) => Balance = allocationValue;

        public ChainSpecAllocation(UInt256 allocationValue, ulong nonce, byte[]? code, byte[]? constructor, Dictionary<UInt256, byte[]>? storage)
        {
            Balance = allocationValue;
            Nonce = nonce;
            Code = code;
            Constructor = constructor;
            Storage = storage;
        }

        public UInt256 Balance { get; set; }

        public ulong Nonce { get; set; }

        public byte[]? Code { get; set; }

        public byte[]? Constructor { get; set; }

        public Dictionary<UInt256, byte[]>? Storage { get; set; }

    }
}
