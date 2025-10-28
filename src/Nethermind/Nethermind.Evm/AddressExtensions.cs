// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Evm.State;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm
{
    public static class ContractAddress
    {
        public static Address From(Address? deployingAddress, in UInt256 nonce)
        {
            int contentLength = Rlp.LengthOf(deployingAddress) + Rlp.LengthOf(nonce);
            RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
            stream.StartSequence(contentLength);
            stream.Encode(deployingAddress);
            stream.Encode(nonce);

            ValueHash256 contractAddressKeccak = ValueKeccak.Compute(stream.Data.AsSpan());

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

        // See https://eips.ethereum.org/EIPS/eip-7610
        public static bool IsNonZeroAccount(this Address contractAddress, IReleaseSpec spec, ICodeInfoRepository codeInfoRepository, IWorldState state)
        {
            return codeInfoRepository.GetCachedCodeInfo(contractAddress, spec).CodeSpan.Length != 0 ||
                   state.GetNonce(contractAddress) != 0 ||
                   !state.IsStorageEmpty(contractAddress);
        }
    }
}
