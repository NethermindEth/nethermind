using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public class WithdrawalDecoder : IRlpStreamDecoder<Withdrawal>, IRlpValueDecoder<Withdrawal>
{
    public Withdrawal Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        new()
        {
            Index = rlpStream.DecodeULong(),
            ValidatorIndex = rlpStream.DecodeULong(),
            Recipient = rlpStream.DecodeAddress(),
            Amount = rlpStream.DecodeUInt256()
        };

    public Withdrawal Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        new()
        {
            Index = decoderContext.DecodeULong(),
            ValidatorIndex = decoderContext.DecodeULong(),
            Recipient = decoderContext.DecodeAddress(),
            Amount = decoderContext.DecodeUInt256()
        };

    public void Encode(RlpStream stream, Withdrawal item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item.Index);
        stream.Encode(item.ValidatorIndex);
        stream.Encode(item.Recipient);
        stream.Encode(item.Amount);
    }

    public Rlp Encode(Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var stream = new RlpStream(GetLength(item, rlpBehaviors));

        Encode(stream, item, rlpBehaviors);

        return new(stream.Data);
    }

    public int GetLength(Withdrawal item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item.Index) +
        Rlp.LengthOf(item.ValidatorIndex) +
        Rlp.LengthOfAddressRlp +
        Rlp.LengthOf(item.Amount);
}
