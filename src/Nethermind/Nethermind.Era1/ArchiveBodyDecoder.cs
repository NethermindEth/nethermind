// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Era1;

internal enum BodyForm
{
    Decoded,
    DecodedAndEncoded,
    EncodedTransactions,
}

internal static class ArchiveBodyDecoder
{
    // Archive entry buffers are reused after decoding, so retained encodings need their own copy.
    internal static (BlockBody, ArrayPoolList<byte>?) Decode(Memory<byte> buffer, BodyForm form, IRlpDecoder<BlockBody> decoder)
    {
        if (form == BodyForm.EncodedTransactions && decoder is BlockBodyDecoder bodyDecoder)
        {
            ArrayPoolList<byte> encoded = new(buffer.Span);
            try
            {
                return (bodyDecoder.DecodeWithEncodedTransactions(encoded.AsMemory())!, encoded);
            }
            catch
            {
                encoded.Dispose();
                throw;
            }
        }

        RlpReader reader = new(buffer.Span);
        BlockBody body = decoder.Decode(ref reader, RlpBehaviors.SkipPooledTransactions)!;
        // The importer stores these bytes as they are, so they must hash to the header's uncles hash.
        (decoder as BlockBodyDecoder)?.CheckCanonicalUncles(buffer.Span, body.Uncles);
        return (body, form == BodyForm.DecodedAndEncoded ? new ArrayPoolList<byte>(buffer.Span) : null);
    }

    internal static void Release(BlockBody body, ArrayPoolList<byte>? encoded, BodyForm form)
    {
        if (encoded is null) return;
        if (form == BodyForm.EncodedTransactions) BlockBodyDecoder.ReturnTransactions(body);
        encoded.Dispose();
    }
}
