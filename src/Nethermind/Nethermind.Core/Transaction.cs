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

using System.Diagnostics;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash}, Value: {Value}, To: {To}, Gas: {GasLimit}")]
    public class Transaction
    {
        private readonly bool _isSystem = false;

        public Transaction() { }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="isSystem"></param>
        /// <remarks>ctor based genesis allocations are treated as system transactions.</remarks>
        public Transaction(bool isSystem)
        {
            _isSystem = isSystem;
        }

        public UInt256 Nonce { get; set; }
        public UInt256 GasPrice { get; set; }
        public long GasLimit { get; set; }
        public Address To { get; set; }
        public UInt256 Value { get; set; }
        public byte[] Data { get; set; }
        public byte[] Init { get; set; }
        public Address SenderAddress { get; set; }
        public Signature Signature { get; set; }
        public bool IsSigned => Signature != null;
        public bool IsContractCreation => Init != null;
        public bool IsMessageCall => Data != null;
        public Keccak Hash { get; set; }
        public PublicKey DeliveredBy { get; set; } // tks: this is added so we do not send the pending tx back to original sources, not used yet
        public UInt256 Timestamp { get; set; }

        public static Keccak CalculateHash(Transaction transaction) => Keccak.Compute(Rlp.Encode(transaction));

        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"{indent}Gas Price: {GasPrice}");
            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}To: {To}");
            builder.AppendLine($"{indent}Nonce: {Nonce}");
            builder.AppendLine($"{indent}Value: {Value}");
            builder.AppendLine($"{indent}Data: {(Data ?? new byte[0]).ToHexString()}");
            builder.AppendLine($"{indent}Init: {(Init ?? new byte[0]).ToHexString()}");
            builder.AppendLine($"{indent}Hash: {Hash}");
            return builder.ToString();
        }

        public override string ToString() => ToString(string.Empty);

        public bool IsSystem() => SenderAddress == Address.SystemUser || _isSystem;
    }
}