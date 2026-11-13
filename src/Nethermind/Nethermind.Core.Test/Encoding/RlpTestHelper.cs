// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Encoding;

public static class RlpTestHelper
{
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
        List<Rlp> result = [];
        for (int index = 0; items.HasNext; index++)
        {
            ReadOnlySpan<byte> item = items.ReadEncodedItem();
            result.Add(new Rlp(path.Length > 0 && path[0] == index ? WithTrailingItem(item, path[1..]) : item.ToArray()));
        }

        if (path.Length == 0) result.Add(Rlp.OfEmptyByteArray);
        return Rlp.Encode(result.ToArray()).Bytes;
    }
}
