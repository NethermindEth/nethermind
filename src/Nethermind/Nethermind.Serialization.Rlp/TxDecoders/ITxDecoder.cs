// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public interface ITxDecoder
{
    TxType Type { get; }

    /// <summary>Decodes the transaction payload that is next in <paramref name="reader"/>, after the type byte of a typed transaction.</summary>
    /// <param name="transactionSequence">The bytes that the transaction hash covers: the type byte and the payload, or the whole legacy sequence.</param>
    /// <param name="source">The memory that holds the bytes of <paramref name="reader"/>, so that decoded values can refer to it; empty to copy them.</param>
    void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);
}
