// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
