// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Encoding;

public static class RlpTestHelper
{
    public static readonly byte[] OfEmptyByteArray = [Rlp.EmptyByteArrayByte];

    public static readonly byte[] OfEmptyList = [Rlp.EmptyListByte];

    /// <summary>Wraps items that are already RLP in a sequence.</summary>
    public static byte[] EncodeSequence(params ReadOnlySpan<byte[]> encodedItems)
    {
        int contentLength = 0;
        foreach (byte[] item in encodedItems)
        {
            contentLength += item.Length;
        }

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        RlpSequence sequence = writer.StartSequence(contentLength);
        foreach (byte[] item in encodedItems)
        {
            writer.WriteEncoded(item);
        }

        writer.EndSequence(sequence);
        return bytes;
    }

    /// <summary>Gets the prefix and payload lengths of the first item, without the checks of the canonical form.</summary>
    public static (int PrefixLength, int ContentLength) PrefixAndContentLength(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        if (reader.IsNextSequence)
        {
            RlpReader content = reader.EnterSequence<OffFlag>(out int itemLength);
            return (itemLength - content.Remaining, content.Remaining);
        }

        int contentLength = reader.ReadBytes<OffFlag>().Length;
        return (reader.BytesRead - contentLength, contentLength);
    }

    /// <summary>Appends an empty byte string to a sequence of <paramref name="encoded"/>.</summary>
    /// <param name="encoded">A sequence item.</param>
    /// <param name="path">The item indexes of the nested sequence to change; empty for the outer sequence.</param>
    /// <returns>The canonical encoding with the extra item, which RLP allows and forward-compatible decoders accept.</returns>
    public static byte[] WithTrailingItem(ReadOnlySpan<byte> encoded, params int[] path)
    {
        RlpReader reader = new(encoded);
        RlpReader items = reader.EnterSequence(out _);
        List<byte[]> result = [];
        for (int index = 0; items.HasNext; index++)
        {
            ReadOnlySpan<byte> item = items.ReadEncodedItem();
            result.Add(path.Length > 0 && path[0] == index ? WithTrailingItem(item, path[1..]) : item.ToArray());
        }

        if (path.Length == 0) result.Add(RlpTestHelper.OfEmptyByteArray);
        return RlpTestHelper.EncodeSequence(result.ToArray());
    }
}
