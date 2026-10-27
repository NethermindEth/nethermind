// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public interface ITxDecoder
{
    TxType Type { get; }

    void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>Pushes the lengths <see cref="Encode{TWriter}"/> pops and returns the total encoded length.</summary>
    /// <remarks>Follows the <see cref="IRlpDecoder{T}"/> length stack contract.</remarks>
    int PopulateLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    void Encode<TWriter>(Transaction transaction, ref TWriter writer, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;
}
