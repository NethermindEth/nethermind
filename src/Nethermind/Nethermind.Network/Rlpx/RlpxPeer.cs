/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Internal.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Microsoft.Extensions.Logging.Console;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx.Handshake;

namespace Nethermind.Network.Rlpx
{
    // TODO: integration tests for this one
    public class RlpxPeer : IRlpxPeer
    {
        private const int PeerConnectionTimeout = 10000;
        private readonly IEncryptionHandshakeService _encryptionHandshakeService;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private IChannel _bootstrapChannel;
        private IEventLoopGroup _bossGroup;

        private bool _isInitialized;
        private IEventLoopGroup _workerGroup;

        public RlpxPeer(IEncryptionHandshakeService encryptionHandshakeService, ISessionManager sessionManager, ILogger logger)
        {
            _encryptionHandshakeService = encryptionHandshakeService;
            _sessionManager = sessionManager;
            _logger = logger;
        }

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
                _bossGroup = new MultithreadEventLoopGroup(1);
                _workerGroup = new MultithreadEventLoopGroup();

                ServerBootstrap bootstrap = new ServerBootstrap();
                bootstrap
                    .Group(_bossGroup, _workerGroup)
                    .Channel<TcpServerSocketChannel>()
                    .ChildOption(ChannelOption.SoBacklog, 100)
                    .Handler(new LoggingHandler("BOSS", LogLevel.TRACE))
                    .ChildHandler(new ActionChannelInitializer<ISocketChannel>(ch => InitializeChannel(ch, EncryptionHandshakeRole.Recipient, null)));

                _bootstrapChannel = await bootstrap.BindAsync(port);
            }
            catch (Exception)
            {
                await Task.WhenAll(_bossGroup.ShutdownGracefullyAsync(), _workerGroup.ShutdownGracefullyAsync());
                throw;
            }
        }

        public async Task Connect(PublicKey remoteId, string host, int port)
        {
            _logger.Log($"Connecting to {remoteId} at {host}:{port}");

            Bootstrap clientBootstrap = new Bootstrap();
            clientBootstrap.Group(_workerGroup);
            clientBootstrap.Channel<TcpSocketChannel>();

            clientBootstrap.Option(ChannelOption.TcpNodelay, true);
            clientBootstrap.Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default);
            clientBootstrap.Option(ChannelOption.ConnectTimeout, TimeSpan.FromMilliseconds(PeerConnectionTimeout));
            clientBootstrap.RemoteAddress(host, port);

            clientBootstrap.Handler(new ActionChannelInitializer<ISocketChannel>(ch => InitializeChannel(ch, EncryptionHandshakeRole.Initiator, remoteId)));

            await clientBootstrap.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));
            _logger.Log($"Connected to {remoteId} at {host}:{port}");
        }

        private void InitializeChannel(IChannel channel, EncryptionHandshakeRole role, PublicKey remoteId)
        {
            string inOut = remoteId == null ? "IN" : "OUT";
            _logger.Log($"Initializing {inOut} channel");

            IChannelPipeline pipeline = channel.Pipeline;
            pipeline.AddLast(new LoggingHandler(inOut, LogLevel.TRACE));
            pipeline.AddLast("enc-handshake-dec", new LengthFieldBasedFrameDecoder(ByteOrder.BigEndian, ushort.MaxValue, 0, 2, 0, 0, true));
            pipeline.AddLast("enc-handshake-handler", new NettyHandshakeHandler(_encryptionHandshakeService, _sessionManager, role, remoteId, _logger));
        }
    }
}