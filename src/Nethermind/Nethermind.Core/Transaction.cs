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

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Core
{
    public class Transaction
    {
        public int ChainId { get; set; }
        public BigInteger Nonce { get; set; }
        public BigInteger GasPrice { get; set; }
        public BigInteger GasLimit { get; set; }
        public Address To { get; set; }
        public BigInteger Value { get; set; }
        public byte[] Data { get; set; }
        public byte[] Init { get; set; }
        public Signature Signature { get; set; }
        public bool IsSigned => Signature != null;
        public bool IsContractCreation => Init != null;
        public bool IsMessageCall => Data != null;
        public bool IsTransfer => !IsContractCreation && !IsMessageCall;
        public bool IsValid { get; set; }
        public Keccak Hash { get; set; }
        public PublicKey EthDeliveredBy { get; set; } // TODO: create a wrapper?

        public static Keccak CalculateHash(Transaction transaction)
        {
            return Keccak.Compute(Rlp.Encode(transaction));
        }
    }
}