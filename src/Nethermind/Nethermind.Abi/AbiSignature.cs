// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Abi
{
    public class AbiSignature(string name, params AbiType[] types)
    {
        private string? _toString;
        private Hash256? _hash;

        public string Name { get; } = name;
        public AbiType[] Types { get; } = types;
        public byte[] Address => GetAddress(Hash.Bytes);
        public Hash256 Hash => _hash ??= Keccak.Compute(ToString());

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

        public static byte[] GetAddress(ReadOnlySpan<byte> bytes) => bytes[..4].ToArray();
    }
}
