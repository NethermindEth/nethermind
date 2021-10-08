/*
 * Copyright (c) 2021 Demerzel Solutions Limited
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
