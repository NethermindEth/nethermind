using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class StatusMessageSerializer : IMessageSerializer<StatusMessage>
    {
        public byte[] Serialize(StatusMessage message, IMessagePad pad = null)
        {
            return Rlp.Encode(
                Rlp.Encode(message.ProtocolVersion),
                Rlp.Encode(message.NetworkId),
                Rlp.Encode(message.TotalDifficulty),
                Rlp.Encode(message.BestHash),
                Rlp.Encode(message.GenesisHash)
            ).Bytes;
        }

        public StatusMessage Deserialize(byte[] bytes)
        {
            StatusMessage statusMessage = new StatusMessage();
            DecodedRlp decoded = Rlp.Decode(new Rlp(bytes));
            statusMessage.ProtocolVersion = decoded.GetInt(0);
            statusMessage.NetworkId = decoded.GetInt(1);
            statusMessage.TotalDifficulty = decoded.GetUnsignedBigInteger(2);
            statusMessage.BestHash = decoded.GetKeccak(3);
            statusMessage.GenesisHash = decoded.GetKeccak(4);
            return statusMessage;
        }
    }
}