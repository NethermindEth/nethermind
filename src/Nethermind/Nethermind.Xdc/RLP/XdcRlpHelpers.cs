// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.RLP;

internal static class XdcRlpHelpers
{
    internal static Address[] DecodeAddressArray(ref RlpReader reader)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        Address[] addresses = new Address[items.Remaining / Rlp.LengthOfAddressRlp];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = items.DecodeAddress();
        }

        reader.ExitSequence(items, itemLength);
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

    internal static void EncodeAddressSequence(ref RlpWriter writer, Address[] addresses)
    {
        int length = addresses.Length;
        RlpSequence sequence = writer.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            writer.Encode(addresses[i]);
        }
        writer.EndSequence(sequence);
    }

    internal static int LengthOfAddressSequence(Address[]? addresses) =>
        Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * (addresses?.Length ?? 0));
}
