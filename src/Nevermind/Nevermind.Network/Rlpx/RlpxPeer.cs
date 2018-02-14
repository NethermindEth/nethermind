using System;
using System.Threading.Tasks;
using DotNetty.Common.Internal.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Microsoft.Extensions.Logging.Console;

namespace Nevermind.Network.Rlpx
{
    public class RlpxPeer
    {
        private IChannel _bootstrapChannel;
        private IEventLoopGroup _bossGroup;

        private bool _isInitialized;
        private IEventLoopGroup _workerGroup;

        public async Task Shutdown()
        {
            InternalLoggerFactory.DefaultFactory.AddProvider(new ConsoleLoggerProvider((s, level) => true, false));
            
            await _bootstrapChannel.CloseAsync();
            await Task.WhenAll(_bossGroup.ShutdownGracefullyAsync(), _workerGroup.ShutdownGracefullyAsync());
        }

        public async Task Init(int port)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException($"{nameof(RlpxPeer)} already initialized.");
            }

            _isInitialized = true;

            try
            {
                _bossGroup = new MultithreadEventLoopGroup();
                _workerGroup = new MultithreadEventLoopGroup();

                ServerBootstrap bootstrap = new ServerBootstrap();
                bootstrap
                    .Group(_bossGroup, _workerGroup)
                    .Channel<TcpServerSocketChannel>()
                    .Option(ChannelOption.TcpNodelay, true)
                    .ChildOption(ChannelOption.SoKeepalive, true)
                    .Handler(new LoggingHandler("BOSS", LogLevel.TRACE))
                    .ChildHandler(new ActionChannelInitializer<ISocketChannel>(InitializeChannel));

                _bootstrapChannel = await bootstrap.BindAsync(port);
            }
            catch (Exception)
            {
                await Task.WhenAll(_bossGroup.ShutdownGracefullyAsync(), _workerGroup.ShutdownGracefullyAsync());
                throw;
            }
        }

        private void InitializeChannel(IChannel channel)
        {
            IChannelPipeline pipeline = channel.Pipeline;
            pipeline.AddLast(new LoggingHandler("CHILD", LogLevel.TRACE));
            // decoders
            // encoders
        }
    }
}