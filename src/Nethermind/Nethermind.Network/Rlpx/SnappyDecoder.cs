using System.Collections.Generic;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Snappy;

namespace Nethermind.Network.Rlpx
{
    public class SnappyDecoder : MessageToMessageDecoder<byte[]>
    {
        protected override void Decode(IChannelHandlerContext context, byte[] message, List<object> output)
        {
            output.Add(SnappyCodec.Uncompress(message));
        }
    }
}