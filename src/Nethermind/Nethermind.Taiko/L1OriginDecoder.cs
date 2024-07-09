using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class L1OriginDecoder : IRlpObjectDecoder<L1Origin>, IRlpStreamDecoder<L1Origin>
{
    public L1Origin Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.SkipLength();

        UInt256 blockId = rlpStream.DecodeUInt256();
        Hash256? l2BlockHash = rlpStream.DecodeKeccak();
        var l1BlockHeight = rlpStream.DecodeLong();
        Hash256 l1BlockHash = rlpStream.DecodeKeccak() ?? throw new RlpException("L1BlockHash is null");

        return new(blockId, l2BlockHash, l1BlockHeight, l1BlockHash);
    }

    public Rlp Encode(L1Origin? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new(rlpStream.Data.ToArray()!);
    }

    public void Encode(RlpStream stream, L1Origin item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetLength(item, rlpBehaviors));

        stream.Encode(item.BlockID);
        stream.Encode(item.L2BlockHash);
        stream.Encode(item.L1BlockHeight);
        stream.Encode(item.L1BlockHash);
    }

    public int GetLength(L1Origin item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(
            Rlp.LengthOf(item.BlockID)
            + Rlp.LengthOf(item.L2BlockHash)
            + Rlp.LengthOf(item.L1BlockHeight)
            + Rlp.LengthOf(item.L1BlockHash));
    }
}
