// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            RlpStream stream = new RlpStream(Rlp.LengthOfSequence(contentLength));
            stream.StartSequence(contentLength);
            stream.Encode(deployingAddress);
            stream.Encode(nonce);

            ValueKeccak contractAddressKeccak = ValueKeccak.Compute(stream.Data.AsSpan());

            return new Address(in contractAddressKeccak);
        }

        public static Address From(Address deployingAddress, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> initCode)
        {
            // sha3(0xff ++ msg.sender ++ salt ++ sha3(init_code)))
            Span<byte> bytes = new byte[1 + Address.ByteLength + 32 + salt.Length];
            bytes[0] = 0xff;
            deployingAddress.Bytes.CopyTo(bytes.Slice(1, 20));
            salt.CopyTo(bytes.Slice(21, salt.Length));
            ValueKeccak.Compute(initCode).BytesAsSpan.CopyTo(bytes.Slice(21 + salt.Length, 32));

            ValueKeccak contractAddressKeccak = ValueKeccak.Compute(bytes);
            return new Address(in contractAddressKeccak);
        }

        public static Address From(Address deployingAddress, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> initCode, ReadOnlySpan<byte> auxData)
        {
            // sha3(0xff ++ msg.sender ++ salt ++ sha3(init_code) ++ sha3(aux_data))
            Span<byte> bytes = new byte[1 + Address.ByteLength + 32 + salt.Length + auxData.Length];
            bytes[0] = 0xff;
            deployingAddress.Bytes.CopyTo(bytes.Slice(1, 20));
            salt.CopyTo(bytes.Slice(21, salt.Length));
            ValueKeccak.Compute(initCode).BytesAsSpan.CopyTo(bytes.Slice(21 + salt.Length, 32));
            auxData.CopyTo(bytes.Slice(21 + salt.Length + 32, auxData.Length));

            ValueKeccak contractAddressKeccak = ValueKeccak.Compute(bytes);
            Span<byte> addressBytes = contractAddressKeccak.BytesAsSpan[12..];
            return new Address(addressBytes.ToArray());
        }
    }
}
