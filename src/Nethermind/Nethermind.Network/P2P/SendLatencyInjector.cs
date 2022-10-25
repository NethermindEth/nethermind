using System;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;

namespace Nethermind.Network.P2P;

public class SendLatencyInjector : ChannelHandlerAdapter
{
    private readonly TimeSpan _sendLatency;

    public SendLatencyInjector(TimeSpan sendLatency)
    {
        _sendLatency = sendLatency;
    }

    public override async Task WriteAsync(IChannelHandlerContext context, object message)
    {
        if (_sendLatency != TimeSpan.Zero)
        {
            await Task.Delay(_sendLatency);
        }

        await base.WriteAsync(context, message);
    }
}
