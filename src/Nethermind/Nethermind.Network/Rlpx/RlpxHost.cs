// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using DotNetty.Common.Concurrency;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Buffers;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Rlpx
{
    public class RlpxHost : IRlpxHost
    {
        private IChannel? _bootstrapChannel;
        private IEventLoopGroup? _bossGroup;
        private IEventLoopGroup? _workerGroup;

        private bool _isInitialized;
        public PublicKey LocalNodeId { get; }
        public int LocalPort { get; }
        private string? LocalIp { get; }
        private readonly IHandshakeService _handshakeService;
        private readonly IMessageSerializationService _serializationService;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly ISessionMonitor _sessionMonitor;
        private readonly IDisconnectsAnalyzer _disconnectsAnalyzer;
        private readonly IEventExecutorGroup _group;
        private readonly TimeSpan _sendLatency;
        private readonly TimeSpan _connectTimeout;
        private readonly IChannelFactory? _channelFactory;

        private readonly TimeSpan _shutdownQuietPeriod;
        private readonly TimeSpan _shutdownCloseTimeout;

        public RlpxHost(
            IMessageSerializationService serializationService,
            [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
            IHandshakeService handshakeService,
            ISessionMonitor sessionMonitor,
            IDisconnectsAnalyzer disconnectsAnalyzer,
            INetworkConfig networkConfig,
            ILogManager logManager,
            IChannelFactory? channelFactory = null)
        {
            // .NET Core definitely got the easy logging setup right :D
            // ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Paranoid;
            // ConfigureNamedOptions<ConsoleLoggerOptions> configureNamedOptions = new("", null);
            // OptionsFactory<ConsoleLoggerOptions> optionsFactory = new(
            //     new []{ configureNamedOptions },
            //     Enumerable.Empty<IPostConfigureOptions<ConsoleLoggerOptions>>());
            // OptionsMonitor<ConsoleLoggerOptions> optionsMonitor = new(
            //     optionsFactory,
            //     Enumerable.Empty<IOptionsChangeTokenSource<ConsoleLoggerOptions>>(),
            //     new OptionsCache<ConsoleLoggerOptions>());
            // LoggerFactory loggerFactory = new(
            //     new[] { new ConsoleLoggerProvider(optionsMonitor) },
            //     new LoggerFilterOptions { MinLevel = Microsoft.Extensions.Logging.LogLevel.Warning });
            // InternalLoggerFactory.DefaultFactory = loggerFactory;

            int networkProcessingThread = networkConfig.ProcessingThreadCount;
            if (networkProcessingThread <= 1)
            {
                _group = new SingleThreadEventLoop();
            }
            else
            {
                _group = new MultithreadEventLoopGroup(networkProcessingThread);
            }
            _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
            _sessionMonitor = sessionMonitor ?? throw new ArgumentNullException(nameof(sessionMonitor));
            _disconnectsAnalyzer = disconnectsAnalyzer ?? throw new ArgumentNullException(nameof(disconnectsAnalyzer));
            _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
            LocalNodeId = nodeKey.PublicKey;
            LocalPort = networkConfig.P2PPort;
            LocalIp = networkConfig.LocalIp;
            _sendLatency = TimeSpan.FromMilliseconds(networkConfig.SimulateSendLatencyMs);
            _connectTimeout = TimeSpan.FromMilliseconds(networkConfig.ConnectTimeoutMs);
            _channelFactory = channelFactory;
            _shutdownQuietPeriod = TimeSpan.FromMilliseconds(Math.Min(networkConfig.RlpxHostShutdownCloseTimeoutMs, 100));
            _shutdownCloseTimeout = TimeSpan.FromMilliseconds(networkConfig.RlpxHostShutdownCloseTimeoutMs);
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
                // Default is LogicalCoreCount * 2
                // - so with two groups and 32 logical cores, we would have 128 threads
                // Max at 8 threads per group for 16 threads total
                // Min of 2 threads per group for 4 threads total
                var threads = Math.Clamp(Environment.ProcessorCount / 2, min: 2, max: 8);
                _bossGroup = new MultithreadEventLoopGroup(threads);
                _workerGroup = new MultithreadEventLoopGroup(threads);

                ServerBootstrap bootstrap = new();
                bootstrap
                    .Group(_bossGroup, _workerGroup)
                    .ChannelFactory(() => _channelFactory?.CreateServer() ?? new TcpServerSocketChannel())
                    .Option(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                    .ChildOption(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                    .ChildOption(ChannelOption.SoBacklog, 100)
                    .ChildOption(ChannelOption.TcpNodelay, true)
                    .ChildOption(ChannelOption.SoTimeout, (int)_connectTimeout.TotalMilliseconds)
                    .ChildOption(ChannelOption.SoKeepalive, true)
                    .ChildOption(ChannelOption.WriteBufferHighWaterMark, (int)3.MB())
                    .ChildOption(ChannelOption.WriteBufferLowWaterMark, (int)1.MB())
                    .Handler(new LoggingHandler("BOSS", LogLevel.TRACE))
                    .ChildHandler(new ActionChannelInitializer<IChannel>(ch =>
                    {
                        Session session = new(LocalPort, ch, _disconnectsAnalyzer, _logManager);
                        IPEndPoint? ipEndPoint = ch.RemoteAddress.ToIPEndpoint();
                        session.RemoteHost = ipEndPoint.Address.ToString();
                        session.RemotePort = ipEndPoint.Port;
                        InitializeChannel(ch, session);
                    }));

                Task<IChannel> openTask = NetworkHelper.HandlePortTakenError(() => LocalIp is null
                        ? bootstrap.BindAsync(LocalPort)
                        : bootstrap.BindAsync(IPAddress.Parse(LocalIp), LocalPort),
                    LocalPort);

                _bootstrapChannel = await openTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error($"{nameof(Init)} failed", t.Exception);
                        return null;
                    }

                    return t.Result;
                });

                if (_bootstrapChannel is null)
                {
                    throw new NetworkingException($"Failed to initialize {nameof(_bootstrapChannel)}", NetworkExceptionType.Other);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"{nameof(Init)} failed.", ex);
                // Replacing to prevent double dispose which hangs
                var bossGroup = Interlocked.Exchange(ref _bossGroup, null);
                var workerGroup = Interlocked.Exchange(ref _workerGroup, null);
                await Task.WhenAll(bossGroup?.ShutdownGracefullyAsync() ?? Task.CompletedTask, workerGroup?.ShutdownGracefullyAsync() ?? Task.CompletedTask);
                throw;
            }
        }

        public async Task ConnectAsync(Node node)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} initiating OUT connection");

            Bootstrap clientBootstrap = new();
            clientBootstrap
                .Group(_workerGroup)
                .ChannelFactory(() => _channelFactory?.CreateClient() ?? new TcpSocketChannel())
                .Option(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                .Option(ChannelOption.TcpNodelay, true)
                .Option(ChannelOption.SoTimeout, (int)_connectTimeout.TotalMilliseconds)
                .Option(ChannelOption.SoKeepalive, true)
                .Option(ChannelOption.WriteBufferHighWaterMark, (int)3.MB())
                .Option(ChannelOption.WriteBufferLowWaterMark, (int)1.MB())
                .Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default)
                .Option(ChannelOption.ConnectTimeout, _connectTimeout);
            clientBootstrap.Handler(new ActionChannelInitializer<IChannel>(ch =>
            {
                Session session = new(LocalPort, node, ch, _disconnectsAnalyzer, _logManager);
                InitializeChannel(ch, session);
            }));

            Task<IChannel> connectTask = clientBootstrap.ConnectAsync(node.Address);
            CancellationTokenSource delayCancellation = new();
            Task firstTask = await Task.WhenAny(connectTask, Task.Delay(_connectTimeout.Add(TimeSpan.FromSeconds(2)), delayCancellation.Token));
            if (firstTask != connectTask)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} OUT connection timed out");

                _ = connectTask.ContinueWith(async c =>
                {
                    if (connectTask.IsCompletedSuccessfully)
                    {
                        await c.Result.DisconnectAsync();
                    }
                });

                throw new NetworkingException($"Failed to connect to {node:s} (timeout)", NetworkExceptionType.Timeout);
            }

            delayCancellation.Cancel();
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
            NettyHandshakeHandler handshakeHandler = new(_serializationService, _handshakeService, session, role, _logManager, _group, _sendLatency);

            IChannelPipeline pipeline = channel.Pipeline;
            pipeline.AddLast(new LoggingHandler(session.Direction.ToString().ToUpper(), LogLevel.TRACE));
            pipeline.AddLast("enc-handshake-dec", new OneTimeLengthFieldBasedFrameDecoder());
            pipeline.AddLast("enc-handshake-handler", handshakeHandler);

            channel.CloseCompletion.ContinueWith(async x =>
            {
                // The close completion is completed before actual closing or remaining packet is processed.
                // So usually, we do get a disconnect reason from peer, we just receive it after this. So w need to
                // add some delay to account for whatever that is holding the network pipeline.
                await Task.Delay(TimeSpan.FromSeconds(1));

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} channel disconnected");
                session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "channel disconnected");
            });
        }

        private void SessionOnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (Session)sender;
            session.Disconnected -= SessionOnPeerDisconnected;
            session.Dispose();
        }

        public async Task Shutdown()
        {
            await (_bootstrapChannel?.CloseAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(Shutdown)} failed", t.Exception);
                }
            }) ?? Task.CompletedTask);

            if (_logger.IsDebug) _logger.Debug("Closed _bootstrapChannel");

            Task closingTask = Task.WhenAll(
                _bossGroup is not null ? _bossGroup.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout) : Task.CompletedTask,
                _workerGroup is not null ? _workerGroup.ShutdownGracefullyAsync(_shutdownCloseTimeout, _shutdownCloseTimeout) : Task.CompletedTask);

            // below comment may arise from not understanding the quiet period but the resolution is correct
            // we need to add additional timeout on our side as netty is not executing internal timeout properly, often it just hangs forever on closing
            CancellationTokenSource delayCancellation = new();
            if (await Task.WhenAny(closingTask, Task.Delay(Timeouts.TcpClose, delayCancellation.Token)) != closingTask)
            {
                if (_logger.IsDebug) _logger.Debug($"Could not close rlpx connection in {Timeouts.TcpClose.TotalSeconds} seconds");
            }
            else
            {
                delayCancellation.Cancel();
            }

            if (_logger.IsInfo) _logger.Info("Local peer shutdown complete.. please wait for all components to close");
        }

        public ISessionMonitor SessionMonitor => _sessionMonitor;
    }
}
