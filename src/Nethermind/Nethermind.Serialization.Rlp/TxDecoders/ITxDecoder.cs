// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public interface ITxDecoder
{
    TxType Type { get; }

    void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>Populates the lengths <see cref="Encode{TWriter}"/> reads and adds the total encoded length to <paramref name="lengths"/>.</summary>
    /// <remarks>Follows the <see cref="IRlpDecoder{T}"/> length list contract.</remarks>
    void PopulateLength(Transaction transaction, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    void Encode<TWriter>(Transaction transaction, ref TWriter writer, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;
}
