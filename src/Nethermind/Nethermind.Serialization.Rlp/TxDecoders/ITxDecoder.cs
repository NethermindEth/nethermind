// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public interface ITxDecoder
{
    public TxType Type { get; }

    public void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public void Encode<TBackend>(Transaction transaction, ref ValueRlpWriter<TBackend> writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        where TBackend : IValueRlpWriteBackend, allows ref struct;

    public int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);
}
