// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.RLP;

internal static class XdcRlpHelpers
{
    internal static Address[] DecodeAddressArray(ref Rlp.ValueDecoderContext decoderContext)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            _ = decoderContext.ReadByte();
            return [];
        }

        int length = decoderContext.ReadSequenceLength();
        Address[] addresses = new Address[length / Rlp.LengthOfAddressRlp];

        int index = 0;
        while (length > 0)
        {
            addresses[index++] = decoderContext.DecodeAddress();
            length -= Rlp.LengthOfAddressRlp;
        }

        return addresses;
    }

    internal static byte[] DecodeAddressBytes(ref Rlp.ValueDecoderContext decoderContext)
    {
        Address[] addresses = DecodeAddressArray(ref decoderContext);
        if (addresses.Length == 0)
        {
            return [];
        }

        byte[] result = new byte[addresses.Length * Address.Size];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i].Bytes.CopyTo(result.AsSpan(i * Address.Size, Address.Size));
        }

        return result;
    }

    internal static void EncodeAddressSequence(RlpStream stream, Address[] addresses)
    {
        int length = addresses.Length;
        stream.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            stream.Encode(addresses[i]);
        }
    }

    internal static int LengthOfAddressSequence(Address[]? addresses) =>
        Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * (addresses?.Length ?? 0));
}
