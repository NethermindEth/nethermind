//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiSignature
    {
        private string? _toString;
        private Keccak? _hash;

        public AbiSignature(string name, params AbiType[] types)
        {
            Name = name;
            Types = types;
        }

        public string Name { get; }
        public AbiType[] Types { get; }
        public byte[] Address => GetAddress(Hash.Bytes);
        public Keccak Hash => _hash ??= Keccak.Compute(ToString());

        public override string ToString()
        {
            string ComputeString()
            {
                string[] argTypeNames = new string[Types.Length];
                for (int i = 0; i < Types.Length; i++)
                {
                    argTypeNames[i] = Types[i].ToString();
                }

                string typeList = string.Join(",", argTypeNames);
                string signatureString = $"{Name}({typeList})";
                return signatureString;
            }

            return _toString ??= ComputeString();
        }

        public static byte[] GetAddress(byte[] bytes) => bytes.Slice(0, 4);
    }
}
