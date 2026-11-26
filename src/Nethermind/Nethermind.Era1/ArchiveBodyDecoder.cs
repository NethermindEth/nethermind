// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Era1;

/// <summary>How an archive reader returns a block body.</summary>
public enum BodyForm
{
    /// <summary>The fully decoded body only.</summary>
    Decoded,
    /// <summary>The fully decoded body and a copy of its encoding.</summary>
    DecodedAndEncoded,
    /// <summary>The body with encoded transactions, from <see cref="BlockBodyDecoder.DecodeWithEncodedTransactions"/>, and the encoding they refer to.</summary>
    EncodedTransactions,
}

/// <summary>Decodes the block bodies of the Era1 and EraE archives.</summary>
public static class ArchiveBodyDecoder
{
    // Archive entry buffers are reused after decoding, so retained encodings need their own copy.
    public static (BlockBody, ArrayPoolList<byte>?) Decode(Memory<byte> buffer, BodyForm form, IRlpDecoder<BlockBody> decoder)
    {
        if (form == BodyForm.EncodedTransactions && decoder is BlockBodyDecoder bodyDecoder)
        {
            ArrayPoolList<byte> encoded = [with(buffer.Span)];
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

    public static void Release(BlockBody body, ArrayPoolList<byte>? encoded, BodyForm form)
    {
        if (encoded is null) return;
        if (form == BodyForm.EncodedTransactions) BlockBodyDecoder.ReturnTransactions(body);
        encoded.Dispose();
    }
}
