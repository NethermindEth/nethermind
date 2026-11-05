// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm
{
    public static class ContractAddress
    {
        /// <summary>The EIP-1014 <c>CREATE2</c> address-preimage prefix.</summary>
        public const byte Create2Prefix = 0xff;

        /// <summary>The EIP-8360 <c>TCREATE</c> address-preimage prefix.</summary>
        public const byte TransientCreatePrefix = 0xfe;

        private const int SaltOffset = 1 + Address.Size;

        public static Address From(Address? deployingAddress, in UInt256 nonce)
        {
            int contentLength = Rlp.LengthOf(deployingAddress) + Rlp.LengthOf(nonce);
            int totalLength = Rlp.LengthOfSequence(contentLength);

            Span<byte> bytes = stackalloc byte[totalLength];
            RlpWriter writer = new(bytes);
            writer.StartSequence(contentLength);
            writer.Encode(deployingAddress);
            writer.Encode(nonce);

            ValueHash256 contractAddressKeccak = ValueKeccak.Compute(bytes[..writer.Position]);

            return new(in contractAddressKeccak);
        }

        public static Address From(Address deployingAddress, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> initCode)
            => FromSalted(Create2Prefix, deployingAddress, salt, initCode);

        /// <summary>Computes the EIP-8360 <c>TCREATE</c> address: <c>keccak256(0xfe ++ deployer ++ salt ++ keccak256(init_code))[12:]</c>.</summary>
        public static Address FromTransientCreate(Address deployingAddress, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> initCode)
            => FromSalted(TransientCreatePrefix, deployingAddress, salt, initCode);

        [SkipLocalsInit]
        private static Address FromSalted(byte prefix, Address deployingAddress, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> initCode)
        {
            // sha3(prefix ++ msg.sender ++ salt ++ sha3(init_code))
            Span<byte> bytes = stackalloc byte[SaltOffset + salt.Length + Keccak.Size];
            bytes[0] = prefix;
            deployingAddress.Bytes.CopyTo(bytes.Slice(1, Address.Size));
            salt.CopyTo(bytes.Slice(SaltOffset, salt.Length));
            ValueKeccak.Compute(initCode).BytesAsSpan.CopyTo(bytes.Slice(SaltOffset + salt.Length, Keccak.Size));

            ValueHash256 contractAddressKeccak = ValueKeccak.Compute(bytes);
            return new(in contractAddressKeccak);
        }
    }
}
