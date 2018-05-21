namespace Nethermind.Core.Encoding
{
    public class NetworkNodeDecoder : IRlpDecoder<NetworkNode>
    {
        public NetworkNode Decode(DecodedRlp rlp)
        {
            var publicKey = new Hex(rlp.GetBytes(0));
            var ip = rlp.GetString(1);
            var port = rlp.GetInt(2);
            var description = rlp.GetString(3);
            var reputation = rlp.GetLong(4);

            var networkNode = new NetworkNode(publicKey, ip, port, description, reputation);    
            return networkNode;
        }
    }
}