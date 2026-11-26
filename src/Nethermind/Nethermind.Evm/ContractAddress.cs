// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
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
            if (deployingAddress is not null && nonce.IsUint64) return From(deployingAddress, nonce.u0);

            int contentLength = Rlp.LengthOf(deployingAddress) + Rlp.LengthOf(nonce);
            int totalLength = Rlp.LengthOfSequence(contentLength);

            Span<byte> bytes = stackalloc byte[totalLength];
            RlpWriter writer = RlpWriter.ContentWriter(bytes);
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(deployingAddress);
            writer.Encode(nonce);
            writer.EndSequence(sequence);

            ValueHash256 contractAddressKeccak = ValueKeccak.Compute(bytes);

            return new(in contractAddressKeccak);
        }

        /// <remarks>
        /// Writes <c>rlp([deployer, nonce])</c> directly: a nonce of at most eight bytes keeps the list under 56 bytes, so
        /// both headers are single bytes.
        /// </remarks>
        [SkipLocalsInit]
        private static Address From(Address deployingAddress, ulong nonce)
        {
            const int AddressItem = 1 + Address.Size;
            Span<byte> bytes = stackalloc byte[1 + AddressItem + 1 + sizeof(ulong)];
            bytes[1] = 0x80 + Address.Size;
            deployingAddress.Bytes.CopyTo(bytes.Slice(2, Address.Size));
            int nonceLength;
            if (nonce is > 0 and < 0x80)
            {
                bytes[1 + AddressItem] = (byte)nonce;
                nonceLength = 1;
            }
            else
            {
                int significant = sizeof(ulong) - BitOperations.LeadingZeroCount(nonce) / 8;
                bytes[1 + AddressItem] = (byte)(0x80 + significant);
                BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(1 + AddressItem + 1), nonce << (8 * (sizeof(ulong) - significant)));
                nonceLength = 1 + significant;
            }

            int contentLength = AddressItem + nonceLength;
            bytes[0] = (byte)(0xc0 + contentLength);
            ValueHash256 contractAddressKeccak = ValueKeccak.Compute(bytes[..(1 + contentLength)]);
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
