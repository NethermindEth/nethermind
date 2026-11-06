// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>Decodes the type-specific items of a transaction sequence for <see cref="BaseTxDecoder.DecodeTransaction{TPayload}"/>.</summary>
/// <remarks>
/// Implemented by structs, so each transaction type gets its own decoder body with no dispatch.
/// <typeparamref name="TCanonicalChecks"/> is <see cref="OffFlag"/> for <see cref="RlpBehaviors.TrustedInput"/>.
/// </remarks>
public interface ITxPayloadDecoder
{
    /// <summary>Decodes the payload fields from <paramref name="reader"/>, which holds only the items of the transaction sequence.</summary>
    /// <param name="source">The memory that holds the bytes of <paramref name="reader"/>, or empty.</param>
    static abstract void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag;

    /// <summary>Decodes the items left in the sequence after the payload.</summary>
    static abstract void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag;
}
