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

        [SkipLocalsInit]
        public static Address From(Address deployingAddress, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> initCode)
        {
            // sha3(0xff ++ msg.sender ++ salt ++ sha3(init_code) ++ sha3(aux_data))
            Span<byte> bytes = stackalloc byte[1 + Address.Size + Keccak.Size + salt.Length];
            bytes[0] = 0xff;
            deployingAddress.Bytes.CopyTo(bytes.Slice(1, Address.Size));
            salt.CopyTo(bytes.Slice(1 + Address.Size, salt.Length));
            ValueKeccak.Compute(initCode).BytesAsSpan.CopyTo(bytes.Slice(1 + Address.Size + salt.Length, Keccak.Size));

            ValueHash256 contractAddressKeccak = ValueKeccak.Compute(bytes);
            return new(in contractAddressKeccak);
        }
    }
}
