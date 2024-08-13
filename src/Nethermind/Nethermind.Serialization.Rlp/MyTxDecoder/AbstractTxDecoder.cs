using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

// NOTE: Due to requiring covariant return types we need to use `abstract class` instead of `interface`
public abstract class AbstractTxDecoder
{
    // NOTE: Implementations can (and will) return more specific types than `Transaction`
    public abstract Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors);

    public abstract Transaction Decode(int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors);

    public abstract void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors);

    public abstract Rlp EncodeTx(Transaction? item, bool forSigning, bool isEip155Enabled, ulong chainId, RlpBehaviors rlpBehaviors);

    public abstract int GetLength(Transaction item, RlpBehaviors rlpBehaviors);
}