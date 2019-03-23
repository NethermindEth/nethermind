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
using DotNetty.Common.Concurrency;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx
{
    public class RlpxPeer : IRlpxPeer
    {
        private IChannel _bootstrapChannel;
        private IEventLoopGroup _bossGroup;
        private IEventLoopGroup _workerGroup;

        private bool _isInitialized;
        public PublicKey LocalNodeId { get; }
        public int LocalPort { get; }
        private readonly IEncryptionHandshakeService _encryptionHandshakeService;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly ISessionMonitor _sessionMonitor;
        private IEventExecutorGroup _group;

        public RlpxPeer(
            PublicKey localNodeId,
            int localPort,
            IEncryptionHandshakeService encryptionHandshakeService,
            ILogManager logManager,
            ISessionMonitor sessionMonitor)
        {
            _group = new SingleThreadEventLoop();
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
            _sessionMonitor = sessionMonitor ?? throw new ArgumentNullException(nameof(sessionMonitor));
            _encryptionHandshakeService = encryptionHandshakeService ?? throw new ArgumentNullException(nameof(encryptionHandshakeService));
            LocalNodeId = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
            LocalPort = localPort;
        }

        public async Task Init()
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException($"{nameof(PeerManager)} already initialized.");
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
                    .Handler(new LoggingHandler("BOSS", DotNetty.Handlers.Logging.LogLevel.TRACE))
                    .ChildHandler(new ActionChannelInitializer<ISocketChannel>(ch =>
                    {
                        Session session = new Session(LocalPort, _logManager, ch);
                        session.RemoteHost = ((IPEndPoint) ch.RemoteAddress).Address.ToString();
                        session.RemotePort = ((IPEndPoint) ch.RemoteAddress).Port;
                        InitializeChannel(ch, session);
                    }));

                _bootstrapChannel = await bootstrap.BindAsync(LocalPort).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error($"{nameof(Init)} failed", t.Exception);
                        return null;
                    }

                    return t.Result;
                });

                if (_bootstrapChannel == null)
                {
                    throw new NetworkingException($"Failed to initialize {nameof(_bootstrapChannel)}", NetworkExceptionType.Other);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"{nameof(Init)} failed.", ex);
                await Task.WhenAll(_bossGroup?.ShutdownGracefullyAsync() ?? Task.CompletedTask, _workerGroup?.ShutdownGracefullyAsync() ?? Task.CompletedTask);
                throw;
            }
        }

        public async Task ConnectAsync(Node node)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} initiating OUT connection");

            Bootstrap clientBootstrap = new Bootstrap();
            clientBootstrap.Group(_workerGroup);
            clientBootstrap.Channel<TcpSocketChannel>();
            clientBootstrap.Option(ChannelOption.TcpNodelay, true);
            clientBootstrap.Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default);
            clientBootstrap.Option(ChannelOption.ConnectTimeout, Timeouts.InitialConnection);
            clientBootstrap.RemoteAddress(node.Host, node.Port);
            clientBootstrap.Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
            {
                Session session = new Session(LocalPort, _logManager, ch, node);
                InitializeChannel(ch, session);
            }));

            var connectTask = clientBootstrap.ConnectAsync(new IPEndPoint(IPAddress.Parse(node.Host), node.Port));
            var firstTask = await Task.WhenAny(connectTask, Task.Delay(Timeouts.InitialConnection.Add(TimeSpan.FromSeconds(10))));
            if (firstTask != connectTask)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} OUT connection timed out");
                throw new NetworkingException($"Failed to connect to {node:s} (timeout)", NetworkExceptionType.Timeout);
            }

            if (connectTask.IsFaulted)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"|NetworkTrace| {node:s} error when OUT connecting {connectTask.Exception}");
                }

                throw new NetworkingException($"Failed to connect to {node:s}", NetworkExceptionType.TargetUnreachable, connectTask.Exception);
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} OUT connected");
        }

        public event EventHandler<SessionEventArgs> SessionCreated;

        private void InitializeChannel(IChannel channel, ISession session)
        {
            if (session.Direction == ConnectionDirection.In)
            {
                Metrics.IncomingConnections++;
            }
            else
            {
                Metrics.OutgoingConnections++;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Initializing {session} channel");

            _sessionMonitor.AddSession(session);
            session.Disconnected += SessionOnPeerDisconnected;
            SessionCreated?.Invoke(this, new SessionEventArgs(session));

            HandshakeRole role = session.Direction == ConnectionDirection.In ? HandshakeRole.Recipient : HandshakeRole.Initiator;
            var handshakeHandler = new NettyHandshakeHandler(_encryptionHandshakeService, session, role, session.RemoteNodeId, _logManager, _group);

            IChannelPipeline pipeline = channel.Pipeline;
            // pipeline.AddLast(new LoggingHandler(session.Direction.ToString().ToUpper(), DotNetty.Handlers.Logging.LogLevel.TRACE));
            pipeline.AddLast("enc-handshake-dec", new LengthFieldBasedFrameDecoder(ByteOrder.BigEndian, ushort.MaxValue, 0, 2, 0, 0, true));
            pipeline.AddLast("enc-handshake-handler", handshakeHandler);

            channel.CloseCompletion.ContinueWith(x =>
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} channel disconnected");
                session.Disconnect(DisconnectReason.ClientQuitting, DisconnectType.Remote);
            });
        }

        private void SessionOnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (Session) sender;
            session.Disconnected -= SessionOnPeerDisconnected;
            session.Dispose();
        }

        public async Task Shutdown()
        {
            // InternalLoggerFactory.DefaultFactory.AddProvider(new ConsoleLoggerProvider((s, level) => true, false));

            await _bootstrapChannel.CloseAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(Shutdown)} failed", t.Exception);
                }
            });

            if (_logger.IsDebug) _logger.Debug("Closed _bootstrapChannel");

            // every [quietPeriod] we check if there were any event in the loop - if none then we can shutdown
            var quietPeriod = TimeSpan.FromMilliseconds(100);
            var nettyCloseTimeout = TimeSpan.FromMilliseconds(1000);
            var closingTask = Task.WhenAll(
                _bossGroup.ShutdownGracefullyAsync(quietPeriod, nettyCloseTimeout),
                _workerGroup.ShutdownGracefullyAsync(nettyCloseTimeout, nettyCloseTimeout));

            // below comment may arise from not understanding the quiet period but the resolution is correct
            // we need to add additional timeout on our side as netty is not executing internal timeout properly, often it just hangs forever on closing
            if (await Task.WhenAny(closingTask, Task.Delay(Timeouts.TcpClose)) != closingTask)
            {
                if (_logger.IsDebug) _logger.Debug($"Could not close rlpx connection in {Timeouts.TcpClose.TotalSeconds} seconds");
            }

            if (_logger.IsInfo) _logger.Info("Local peer shutdown complete.. please wait for all components to close");
        }
    }
}