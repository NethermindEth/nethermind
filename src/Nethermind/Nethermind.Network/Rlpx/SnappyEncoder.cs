using System.Collections.Generic;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Snappy;

namespace Nethermind.Network.Rlpx
{
    public class SnappyEncoder : MessageToMessageEncoder<byte[]>
    {
        protected override void Encode(IChannelHandlerContext context, byte[] message, List<object> output)
        {
            output.Add(SnappyCodec.Compress(message));
        }
    }
}