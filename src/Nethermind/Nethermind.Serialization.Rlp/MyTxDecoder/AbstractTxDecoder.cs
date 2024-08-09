using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

// NOTE: Due to requiring covariant return types we need to use `abstract class` instead of `interface`
public abstract class AbstractTxDecoder
{
    // NOTE: Implementations can (and will) return more specific types than `Transaction`
    public abstract Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors);

    public abstract void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors);
}

