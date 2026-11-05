// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>Decodes the type-specific items of a transaction sequence for <see cref="BaseTxDecoder.DecodeTransaction{TPayload}"/>.</summary>
/// <remarks>Implemented by structs, so each transaction type gets its own decoder body with no dispatch.</remarks>
public interface ITxPayloadDecoder
{
    /// <summary>Decodes the payload fields, up to <paramref name="payloadEnd"/>.</summary>
    /// <remarks>The reader can span a whole message, so a decoder sizing an allocation from the bytes on hand
    /// must bound it by <paramref name="payloadEnd"/> and not by the reader's length.</remarks>
    static abstract void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors);

    /// <summary>Decodes the items left in the sequence after the payload.</summary>
    static abstract void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors);
}
