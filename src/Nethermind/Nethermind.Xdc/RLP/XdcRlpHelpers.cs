// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.RLP;

internal static class XdcRlpHelpers
{
    internal static Address[] DecodeAddressArray(ref RlpReader reader)
    {
        if (reader.IsNextItemEmptyList())
        {
            _ = reader.ReadByte();
            return [];
        }

        int length = reader.ReadSequenceLength();
        Address[] addresses = new Address[length / Rlp.LengthOfAddressRlp];

        int index = 0;
        while (length > 0)
        {
            addresses[index++] = reader.DecodeAddress();
            length -= Rlp.LengthOfAddressRlp;
        }

        return addresses;
    }

    internal static byte[] DecodeAddressBytes(ref RlpReader reader)
    {
        Address[] addresses = DecodeAddressArray(ref reader);
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

    internal static void EncodeAddressSequence<TWriter>(ref TWriter writer, Address[] addresses)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int length = addresses.Length;
        writer.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            writer.Encode(addresses[i]);
        }
    }

    internal static int LengthOfAddressSequence(Address[]? addresses) =>
        Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * (addresses?.Length ?? 0));
}
