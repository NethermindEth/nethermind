// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core;

namespace Ethereum.Test.Base
{
    public class IncomingTransaction
    {
        public byte[] Data { get; set; }
        public BigInteger GasLimit { get; set; }
        public BigInteger GasPrice { get; set; }
        public BigInteger Nonce { get; set; }
        public Address To { get; set; }
        public BigInteger Value { get; set; }
        public byte[] R { get; set; }
        public byte[] S { get; set; }
        public byte V { get; set; }
    }
}
