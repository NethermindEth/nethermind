using System;
using System.IO;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class EthRequestDecoder : IRlpDecoder<EthRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        public EthRequestDecoder()
        {
        }

        static EthRequestDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(EthRequest)] = new EthRequestDecoder();
        }
        
        public EthRequest Decode(Nethermind.Core.Encoding.Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = context.DecodeKeccak();
            var host = context.DecodeString();
            var address = context.DecodeAddress();
            var value = context.DecodeUInt256();
            var requestedAt = DateTimeOffset.FromUnixTimeSeconds(context.DecodeLong()).UtcDateTime;
            var transactionHash = context.DecodeKeccak();

            return new EthRequest(id, host, address, value, requestedAt, transactionHash);
        }

        public Nethermind.Core.Encoding.Rlp Encode(EthRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.Host),
                Nethermind.Core.Encoding.Rlp.Encode(item.Address),
                Nethermind.Core.Encoding.Rlp.Encode(item.Value),
                Nethermind.Core.Encoding.Rlp.Encode(new DateTimeOffset(item.RequestedAt).ToUnixTimeSeconds()),
                Nethermind.Core.Encoding.Rlp.Encode(item.TransactionHash));
        }

        public void Encode(MemoryStream stream, EthRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(EthRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}