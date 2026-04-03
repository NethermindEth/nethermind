// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
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
        private readonly Func<IServerChannel> _createServerChannel;
        private readonly Func<IChannel> _createClientChannel;
        private readonly Action<Task<IChannel>, object?> _disconnectConnectedChannel;
        private readonly Action<Task, object?> _onChannelCloseCompleted;
        private readonly Action<Task, object?> _markDisconnectedAfterCloseDelay;
        private readonly NodeFilter _nodeFilter;
        private readonly ConcurrentDictionary<Guid, SessionActivitySubscription> _sessionActivitySubscriptions = new();
        private readonly TimeSpan _shutdownQuietPeriod;
        private readonly TimeSpan _shutdownCloseTimeout;
        private CancellationTokenSource? _shutdownCts = new();

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
            ArgumentNullException.ThrowIfNull(serializationService);
            ArgumentNullException.ThrowIfNull(nodeKey);
            ArgumentNullException.ThrowIfNull(handshakeService);
            ArgumentNullException.ThrowIfNull(sessionMonitor);
            ArgumentNullException.ThrowIfNull(disconnectsAnalyzer);
            ArgumentNullException.ThrowIfNull(networkConfig);
            ArgumentNullException.ThrowIfNull(logManager);

            int networkProcessingThread = networkConfig.ProcessingThreadCount;
            if (networkProcessingThread <= 1)
            {
                _group = new SingleThreadEventLoop();
            }
            else
            {
                _group = new MultithreadEventLoopGroup(networkProcessingThread);
            }
            _serializationService = serializationService;
            _logManager = logManager;
            _logger = logManager.GetClassLogger<RlpxHost>();
            _sessionMonitor = sessionMonitor;
            _disconnectsAnalyzer = disconnectsAnalyzer;
            _handshakeService = handshakeService;
            LocalNodeId = nodeKey.PublicKey;
            LocalPort = networkConfig.P2PPort;
            LocalIp = networkConfig.LocalIp;
            _sendLatency = TimeSpan.FromMilliseconds(networkConfig.SimulateSendLatencyMs);
            _connectTimeout = TimeSpan.FromMilliseconds(networkConfig.ConnectTimeoutMs);
            _channelFactory = channelFactory;
            _createServerChannel = CreateServerChannel;
            _createClientChannel = CreateClientChannel;
            _disconnectConnectedChannel = DisconnectConnectedChannel;
            _onChannelCloseCompleted = OnChannelCloseCompleted;
            _markDisconnectedAfterCloseDelay = MarkDisconnectedAfterCloseDelay;
            _shutdownQuietPeriod = TimeSpan.FromMilliseconds(Math.Min(networkConfig.RlpxHostShutdownCloseTimeoutMs, 100));
            _shutdownCloseTimeout = TimeSpan.FromMilliseconds(networkConfig.RlpxHostShutdownCloseTimeoutMs);
            IPAddress? currentIp = IPAddress.TryParse(networkConfig.ExternalIp ?? networkConfig.LocalIp, out IPAddress? ip) ? ip : null;
            _nodeFilter = NodeFilter.Create(networkConfig.MaxActivePeers, networkConfig.FilterPeersByRecentIp, networkConfig.FilterPeersBySameSubnet, currentIp);
        }

        public bool ShouldContact(IPAddress ip, bool exactOnly = false) => _nodeFilter.TryAccept(ip, exactOnly);

        public async Task Init()
        {
            if (_isInitialized)
            {
                ThrowAlreadyInitialized();
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
                    .ChannelFactory(_createServerChannel)
                    .Option(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                    .Option(ChannelOption.SoBacklog, 100)
                    .ChildOption(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                    .ChildOption(ChannelOption.TcpNodelay, true)
                    .ChildOption(ChannelOption.SoKeepalive, true)
                    .ChildOption(ChannelOption.WriteBufferHighWaterMark, (int)3.MB)
                    .ChildOption(ChannelOption.WriteBufferLowWaterMark, (int)1.MB)
                    .Handler(new LoggingHandler("BOSS", LogLevel.TRACE))
                    .ChildHandler(new InboundChannelInitializer(this));

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
                    ThrowBootstrapChannelInitializationFailed();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"{nameof(Init)} failed.", ex);
                // Replacing to prevent double dispose which hangs
                var bossGroup = Interlocked.Exchange(ref _bossGroup, null);
                var workerGroup = Interlocked.Exchange(ref _workerGroup, null);
                await Task.WhenAll(
                    bossGroup?.ShutdownGracefullyAsync() ?? Task.CompletedTask,
                    workerGroup?.ShutdownGracefullyAsync() ?? Task.CompletedTask,
                    _group.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout));
                throw;
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowAlreadyInitialized()
            => throw new InvalidOperationException($"{nameof(RlpxHost)} already initialized.");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowBootstrapChannelInitializationFailed()
            => throw new NetworkingException($"Failed to initialize {nameof(_bootstrapChannel)}", NetworkExceptionType.Other);

        private IServerChannel CreateServerChannel()
            => _channelFactory?.CreateServer() ?? new TcpServerSocketChannel();

        private IChannel CreateClientChannel()
            => _channelFactory?.CreateClient() ?? new TcpSocketChannel();

        public async Task<bool> ConnectAsync(Node node)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} initiating OUT connection");

            Bootstrap clientBootstrap = new();
            clientBootstrap
                .Group(_workerGroup)
                .ChannelFactory(_createClientChannel)
                .Option(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                .Option(ChannelOption.TcpNodelay, true)
                .Option(ChannelOption.SoKeepalive, true)
                .Option(ChannelOption.WriteBufferHighWaterMark, (int)3.MB)
                .Option(ChannelOption.WriteBufferLowWaterMark, (int)1.MB)
                .Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default)
                .Option(ChannelOption.ConnectTimeout, _connectTimeout);
            clientBootstrap.Handler(new OutboundChannelInitializer(this, node));

            Task<IChannel> connectTask = clientBootstrap.ConnectAsync(node.Address);
            using CancellationTokenSource delayCancellation = new();
            Task firstTask = await Task.WhenAny(connectTask, Task.Delay(_connectTimeout.Add(TimeSpan.FromSeconds(2)), delayCancellation.Token));
            if (firstTask != connectTask)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} OUT connection timed out");

                _ = connectTask.ContinueWith(
                    _disconnectConnectedChannel,
                    null,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                if (_logger.IsDebug) _logger.Debug($"Failed to connect to {node:s} (timeout)");
                return false;
            }

            delayCancellation.Cancel();
            if (connectTask.IsFaulted)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"|NetworkTrace| {node:s} error when OUT connecting {connectTask.Exception}");
                }

                if (_logger.IsDebug) _logger.Debug($"Failed to connect to {node:s}: {connectTask.Exception.Message}");
                return false;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node:s} OUT connected");
            return true;
        }

        public event EventHandler<SessionEventArgs> SessionCreated;

        internal SessionActivitySubscription TrackSessionActivity(ISession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            if (_sessionActivitySubscriptions.TryRemove(session.SessionId, out SessionActivitySubscription? existingSubscription))
            {
                existingSubscription.Detach();
            }

            SessionActivitySubscription subscription = new(this, session);
            _sessionActivitySubscriptions[session.SessionId] = subscription;
            subscription.Attach();
            return subscription;
        }

        /// <summary>
        /// Rejects inbound connections from IPs already seen within the filter window.
        /// Outgoing connections are filtered earlier by <see cref="ShouldContact"/> before <see cref="ConnectAsync"/>.
        /// </summary>
        private bool ShouldRejectInbound(ISession session, IChannel channel)
        {
            if (session.Direction == ConnectionDirection.In
                && channel.RemoteAddress is IPEndPoint remoteEndpoint
                && !_nodeFilter.TryAccept(remoteEndpoint.Address))
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Rejecting inbound connection from filtered IP {remoteEndpoint.Address}");
                _ = channel.CloseAsync();
                return true;
            }

            return false;
        }

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

            if (ShouldRejectInbound(session, channel))
            {
                return;
            }

            SessionActivitySubscription sessionActivitySubscription = TrackSessionActivity(session);
            _sessionMonitor.AddSession(session);
            sessionActivitySubscription.AttachDisconnected();
            SessionCreated?.Invoke(this, new SessionEventArgs(session));

            HandshakeRole role = session.Direction == ConnectionDirection.In ? HandshakeRole.Recipient : HandshakeRole.Initiator;
            NettyHandshakeHandler handshakeHandler = new(_serializationService, _handshakeService, session, role, _logManager, _group, _sendLatency);

            IChannelPipeline pipeline = channel.Pipeline;
            pipeline.AddLast(new LoggingHandler(session.Direction.ToString().ToUpper(), LogLevel.TRACE));
            pipeline.AddLast("enc-handshake-dec", new OneTimeLengthFieldBasedFrameDecoder());
            pipeline.AddLast("enc-handshake-handler", handshakeHandler);

            _ = channel.CloseCompletion.ContinueWith(
                _onChannelCloseCompleted,
                session,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisconnectConnectedChannel(Task<IChannel> connectTask, object? _)
        {
            if (!connectTask.IsCompletedSuccessfully)
            {
                return;
            }

            _ = connectTask.Result.DisconnectAsync();
        }

        private void OnChannelCloseCompleted(Task _, object? state)
        {
            // The close completion is completed before actual closing or remaining packet is processed.
            // So usually, we do get a disconnect reason from peer, we just receive it after this. So we need to
            // add some delay to account for whatever is holding the network pipeline.
            _ = Task.Delay(TimeSpan.FromSeconds(1), _shutdownCts?.Token ?? CancellationToken.None).ContinueWith(
                _markDisconnectedAfterCloseDelay,
                state,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void MarkDisconnectedAfterCloseDelay(Task _, object? state)
        {
            ISession session = (ISession)state!;
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} channel disconnected");
            session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "channel disconnected");
        }

        public async Task Shutdown()
        {
            CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);

            // Close channels first so Disconnected handlers fire while subscriptions are still active
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
                _workerGroup is not null ? _workerGroup.ShutdownGracefullyAsync(_shutdownCloseTimeout, _shutdownCloseTimeout) : Task.CompletedTask,
                _group.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout));

            // below comment may arise from not understanding the quiet period but the resolution is correct
            // we need to add additional timeout on our side as netty is not executing internal timeout properly, often it just hangs forever on closing
            using CancellationTokenSource delayCancellation = new();
            if (await Task.WhenAny(closingTask, Task.Delay(Timeouts.TcpClose, delayCancellation.Token)) != closingTask)
            {
                if (_logger.IsDebug) _logger.Debug($"Could not close rlpx connection in {Timeouts.TcpClose.TotalSeconds} seconds");
            }
            else
            {
                delayCancellation.Cancel();
            }

            // Detach subscriptions and dispose any sessions that weren't disconnected during shutdown.
            // Sessions whose Disconnected event fired are already disposed via OnDisconnected.
            foreach (SessionActivitySubscription subscription in _sessionActivitySubscriptions.Values)
            {
                subscription.DetachAndDispose();
            }

            _sessionActivitySubscriptions.Clear();

            if (_logger.IsInfo) _logger.Info("Local peer shutdown complete.. please wait for all components to close");
        }

        internal sealed class SessionActivitySubscription
        {
            private readonly RlpxHost _rlpxHost;
            private readonly ISession _session;
            private readonly EventHandler<DisconnectEventArgs> _onDisconnected;
            private readonly EventHandler<PeerEventArgs> _refreshNodeFilter;

            public SessionActivitySubscription(RlpxHost rlpxHost, ISession session)
            {
                _rlpxHost = rlpxHost;
                _session = session;
                _onDisconnected = OnDisconnected;
                _refreshNodeFilter = RefreshNodeFilter;
            }

            public void Attach()
            {
                _session.MsgReceived += _refreshNodeFilter;
                _session.MsgDelivered += _refreshNodeFilter;
            }

            public void AttachDisconnected()
            {
                _session.Disconnected += _onDisconnected;
            }

            public void Detach()
            {
                _session.MsgReceived -= _refreshNodeFilter;
                _session.MsgDelivered -= _refreshNodeFilter;
                _session.Disconnected -= _onDisconnected;
                _rlpxHost._sessionActivitySubscriptions.TryRemove(_session.SessionId, out _);
            }

            public void DetachAndDispose()
            {
                Detach();
                try
                {
                    _session.MarkDisconnected(DisconnectReason.AppClosing, DisconnectType.Local, "shutdown");
                    _session.Dispose();
                }
                catch (InvalidOperationException)
                {
                    // Session may already be disposed or in a state that doesn't allow disposal
                }
            }

            private void RefreshNodeFilter(object? _, PeerEventArgs __)
            {
                Node remoteNode = _session.Node;
                _rlpxHost._nodeFilter.Touch(remoteNode.Address.Address, remoteNode.IsStatic || remoteNode.IsBootnode);
            }

            public void OnDisconnected(object? _, DisconnectEventArgs __)
            {
                Detach();
                _session.Dispose();
            }
        }

        private sealed class InboundChannelInitializer : ChannelInitializer<IChannel>
        {
            private readonly RlpxHost _rlpxHost;

            public InboundChannelInitializer(RlpxHost rlpxHost)
            {
                _rlpxHost = rlpxHost;
            }

            protected override void InitChannel(IChannel channel)
            {
                Session session = new(_rlpxHost.LocalPort, channel, _rlpxHost._disconnectsAnalyzer, _rlpxHost._logManager);
                IPEndPoint? ipEndPoint = channel.RemoteAddress.ToIPEndpoint();
                session.RemoteHost = ipEndPoint.Address.ToString();
                session.RemotePort = ipEndPoint.Port;
                _rlpxHost.InitializeChannel(channel, session);
            }
        }

        private sealed class OutboundChannelInitializer : ChannelInitializer<IChannel>
        {
            private readonly RlpxHost _rlpxHost;
            private readonly Node _node;

            public OutboundChannelInitializer(RlpxHost rlpxHost, Node node)
            {
                _rlpxHost = rlpxHost;
                _node = node;
            }

            protected override void InitChannel(IChannel channel)
            {
                Session session = new(_rlpxHost.LocalPort, _node, channel, _rlpxHost._disconnectsAnalyzer, _rlpxHost._logManager);
                _rlpxHost.InitializeChannel(channel, session);
            }
        }

        public ISessionMonitor SessionMonitor => _sessionMonitor;
    }
}
