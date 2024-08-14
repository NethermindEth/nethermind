// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

interface ITxDecoder
{
    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors);

    public void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public void EncodeTx(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    public int GetTxLength(Transaction tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);
}
