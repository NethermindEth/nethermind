// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class LegacyTransactionJson
    {
        public byte[] Data { get; set; }
        public long GasLimit { get; set; }
        public UInt256 GasPrice { get; set; }
        public UInt256 Nonce { get; set; }
        public Address To { get; set; }
        public UInt256 Value { get; set; }
        public byte[] R { get; set; }
        public byte[] S { get; set; }
        public ulong V { get; set; }
    }
}
