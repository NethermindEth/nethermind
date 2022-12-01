// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            ValueKeccak contractAddressKeccak =
                ValueKeccak.Compute(
                    Rlp.Encode(
                        Rlp.Encode(deployingAddress),
                        Rlp.Encode(nonce)).Bytes);

            return new Address(in contractAddressKeccak);
        }

        public static Address From(Address deployingAddress, Span<byte> salt, Span<byte> initCode)
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
    }
}
