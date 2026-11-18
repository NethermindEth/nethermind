// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Concurrency;
using DotNetty.Common.Internal.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Rlpx
{
    public class RlpxHost : IRlpxHost, ISessionActivityObserver, IStoppableService
    {
        private IChannel? _bootstrapChannel;
        private IEventLoopGroup? _bossGroup;
        private IEventLoopGroup? _workerGroup;

        private bool _isInitialized;
        public int LocalPort { get; }
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
        private readonly Func<IChannel> _createClientChannel;
        private readonly Action<Task<IChannel>, object?> _disconnectConnectedChannel;
        private readonly Action<Task, object?> _onChannelCloseCompleted;
        private readonly Action<Task, object?> _markDisconnectedAfterCloseDelay;
        private readonly NodeFilter _nodeFilter;
        private readonly InboundNodeFilter? _inboundNodeFilter;
        private readonly IPrivilegedIpProvider _privilegedIpProvider;
        private readonly NetworkListenerState _listenerState;
        private readonly IIPResolver.NethermindIp _localAddresses;
        private readonly PeerAddressPolicy _addressPolicy;
        private readonly ConcurrentDictionary<Guid, SessionActivitySubscription> _sessionActivitySubscriptions = new();
        private readonly ConcurrentDictionary<IChannel, byte> _activeChannels = new();
        private readonly Lock _activeChannelsLock = new();
        private bool _closingActiveChannels;
        private readonly TaskCompletionSource<bool> _activeChannelsClosing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<Task, object?> _untrackChannel;
        private readonly TimeSpan _shutdownQuietPeriod;
        private readonly TimeSpan _shutdownCloseTimeout;
        private CancellationTokenSource? _shutdownCts = new();
        private readonly int _maxPendingInboundConnections;
        private readonly int _maxPendingInboundConnectionsPerSource;
        private readonly PendingInboundSlots _pendingSlots;
        private readonly Lock _dialLock = new();

        internal int PendingInboundConnections => _pendingSlots.Count;

        /// <summary>Creates the transport host using the shared peer address policy.</summary>
        public RlpxHost(
            PeerAddressPolicy addressPolicy,
            IMessageSerializationService serializationService,
            IHandshakeService handshakeService,
            ISessionMonitor sessionMonitor,
            IDisconnectsAnalyzer disconnectsAnalyzer,
            INetworkConfig networkConfig,
            IIPResolver ipResolver,
            IPrivilegedIpProvider privilegedIpProvider,
            ILogManager logManager,
            NetworkListenerState listenerState,
            IChannelFactory? channelFactory = null)
        {
            ArgumentNullException.ThrowIfNull(addressPolicy);
            ArgumentNullException.ThrowIfNull(serializationService);
            ArgumentNullException.ThrowIfNull(handshakeService);
            ArgumentNullException.ThrowIfNull(sessionMonitor);
            ArgumentNullException.ThrowIfNull(disconnectsAnalyzer);
            ArgumentNullException.ThrowIfNull(networkConfig);
            ArgumentNullException.ThrowIfNull(logManager);
            _maxPendingInboundConnections = NetworkConfig.ValidateMaxPendingInboundConnections(networkConfig.MaxPendingInboundConnections);
            _maxPendingInboundConnectionsPerSource = NetworkConfig.ValidateMaxPendingInboundConnectionsPerSource(networkConfig.MaxPendingInboundConnectionsPerSource);
            _pendingSlots = new(_maxPendingInboundConnections, _maxPendingInboundConnectionsPerSource);

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
            _listenerState = listenerState;
            LocalPort = networkConfig.P2PPort;
            // The peer filter needs the resolved external address before it can accept connections.
            IIPResolver.NethermindIp ips = ipResolver.Resolve().GetAwaiter().GetResult();
            _localAddresses = ips;
            _addressPolicy = addressPolicy;
            _sendLatency = TimeSpan.FromMilliseconds(networkConfig.SimulateSendLatencyMs);
            _connectTimeout = TimeSpan.FromMilliseconds(networkConfig.ConnectTimeoutMs);
            _channelFactory = channelFactory;
            _createClientChannel = CreateClientChannel;
            _disconnectConnectedChannel = DisconnectConnectedChannel;
            _onChannelCloseCompleted = OnChannelCloseCompleted;
            _markDisconnectedAfterCloseDelay = MarkDisconnectedAfterCloseDelay;
            _untrackChannel = UntrackChannel;
            _shutdownQuietPeriod = TimeSpan.FromMilliseconds(Math.Min(networkConfig.RlpxHostShutdownCloseTimeoutMs, 100));
            _shutdownCloseTimeout = TimeSpan.FromMilliseconds(networkConfig.RlpxHostShutdownCloseTimeoutMs);
            _privilegedIpProvider = privilegedIpProvider;
            _nodeFilter = NodeFilter.Create(networkConfig.MaxActivePeers, networkConfig.FilterPeersByRecentIp, networkConfig.FilterPeersBySameSubnet, ips.ExternalIp);
            if (networkConfig.FilterPeersByRecentIp)
                _inboundNodeFilter = new(InboundNodeFilter.DefaultCapacity, !networkConfig.FilterPeersBySameSubnet, ips.ExternalIp);
        }

        // Privileged addresses (static and trusted nodes) bypass the recent-IP filter so they can always connect.
        public bool ShouldContact(IPAddress ip, bool exactOnly = false)
            => _privilegedIpProvider.IsPrivileged(ip) || (!HasLiveConnection(ip, exactOnly) && _nodeFilter.TryAccept(ip, exactOnly));

        private bool HasLiveConnection(IPAddress address, bool exactOnly = false)
        {
            if (_inboundNodeFilter is null) return false;
            NodeFilter.IpSubnetKey key = _nodeFilter.GetKey(address, exactOnly);
            foreach ((Guid _, SessionActivitySubscription subscription) in _sessionActivitySubscriptions)
            {
                if (subscription.RemoteIp is { } remoteIp && subscription.IsOpen && subscription.OwnsSource &&
                    _nodeFilter.GetKey(remoteIp, exactOnly) == key)
                    return true;
            }
            return false;
        }

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
                int threads = Math.Clamp(Environment.ProcessorCount / 2, min: 2, max: 8);
                _bossGroup = new MultithreadEventLoopGroup(threads);
                _workerGroup = new MultithreadEventLoopGroup(threads);

                _bootstrapChannel = await BindListenerAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"{nameof(Init)} failed.", ex);
                // Replacing to prevent double dispose which hangs
                IEventLoopGroup bossGroup = Interlocked.Exchange(ref _bossGroup, null);
                IEventLoopGroup workerGroup = Interlocked.Exchange(ref _workerGroup, null);
                await Task.WhenAll(
                    bossGroup?.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout) ?? Task.CompletedTask,
                    workerGroup?.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout) ?? Task.CompletedTask,
                    _group.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout));
                throw;
            }
        }

        private async Task<IChannel> BindListenerAsync()
        {
            IPAddress bindAddress = _listenerState.PreferredAddress;
            try
            {
                return await BindAsync(bindAddress);
            }
            catch (Exception e) when (!bindAddress.Equals(_listenerState.FallbackAddress))
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to bind {nameof(RlpxHost)} on {bindAddress:hide}:{LocalPort:hide}. Retrying on {_listenerState.FallbackAddress:hide}:{LocalPort:hide}. {e:hide}");
                return await BindAsync(_listenerState.FallbackAddress);
            }
        }

        private async Task<IChannel> BindAsync(IPAddress address)
        {
            IServerChannel? createdChannel = null;
            ServerBootstrap bootstrap = CreateServerBootstrap(() => createdChannel = CreateServerChannel(address));
            try
            {
                IChannel channel = await NetworkHelper.HandlePortTakenError(() => bootstrap.BindAsync(address, LocalPort), LocalPort);
                IPEndPoint? endpoint = channel.TryGetLocalIPEndpoint();
                if (endpoint is not null)
                {
                    _ = _listenerState.TrackRlpxAddress(endpoint.Address, channel.CloseCompletion);
                }

                return channel;
            }
            catch
            {
                await createdChannel.CloseFailedBindAsync(_logger, nameof(RlpxHost));
                throw;
            }
        }

        private ServerBootstrap CreateServerBootstrap(Func<IServerChannel> channelFactory)
            => new ServerBootstrap()
                .Group(_bossGroup, _workerGroup)
                .ChannelFactory(channelFactory)
                .Option(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                .Option(ChannelOption.SoBacklog, 100)
                .ChildOption(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                .ChildOption(ChannelOption.WriteBufferHighWaterMark, (int)3.MB)
                .ChildOption(ChannelOption.WriteBufferLowWaterMark, (int)1.MB)
                .Handler(new LoggingHandler("BOSS", LogLevel.TRACE))
                .ChildHandler(new InboundChannelInitializer(this));

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowAlreadyInitialized()
            => throw new InvalidOperationException($"{nameof(RlpxHost)} already initialized.");

        private IServerChannel CreateServerChannel(IPAddress address)
        {
            if (_channelFactory is not null)
            {
                return _channelFactory.CreateServer();
            }

            return new TcpServerSocketChannel(CreateServerSocket(address));
        }

        private static Socket CreateServerSocket(IPAddress address)
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = DiscoveryAddressSupport.SupportsFamily(address, AddressFamily.InterNetwork);
                    if (socket.DualMode)
                    {
                        // A dual-mode bind must not share its IPv4 port, or the advertised capability is ambiguous.
                        socket.ExclusiveAddressUse = true;
                    }
                }

                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private IChannel CreateClientChannel()
            => _channelFactory?.CreateClient() ?? new TcpSocketChannel();

        public async Task<bool> ConnectAsync(Node node, CancellationToken cancellationToken = default)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} initiating OUT connection");

            PeerAddressPermissions allowNonRoutable = AllowsNonRoutableDial(node);
            bool primaryAccepted = IsDialEndpointAcceptable(node.Address, allowNonRoutable);
            if (!primaryAccepted)
            {
                if (_logger.IsTrace) TraceRejectedDialEndpoint(node, node.Address);
            }
            else if (await TryConnect(node, cancellationToken))
            {
                return true;
            }

            if (!TryGetEligibleAlternate(node, allowNonRoutable, out Node? alternate))
            {
                return false;
            }

            bool exactOnly = alternate.IsStatic || alternate.IsBootnode;
            bool privileged = _privilegedIpProvider.IsPrivileged(alternate.Address.Address);
            if (!privileged && !_nodeFilter.WouldAccept(alternate.Address.Address, exactOnly))
            {
                if (_logger.IsTrace) TraceFilteredDialEndpoint(alternate);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_logger.IsDebug)
            {
                string reason = primaryAccepted ? "Failed to connect" : "Rejected dial endpoint";
                _logger.Debug($"{reason} for {node.ToString("s"):hide} on {node.Address:hide}, retrying on {alternate.Address:hide}");
            }

            bool connected = await TryConnect(alternate, cancellationToken);
            if (connected && !privileged)
            {
                _nodeFilter.Touch(alternate.Address.Address, exactOnly);
            }

            return connected;
        }

        /// <inheritdoc/>
        public bool IsEndpointRejected(Node node)
        {
            PeerAddressPermissions allowNonRoutable = AllowsNonRoutableDial(node);
            return !IsDialEndpointAcceptable(node.Address, allowNonRoutable) &&
                !TryGetEligibleAlternate(node, allowNonRoutable, out _);
        }

        private PeerAddressPermissions AllowsNonRoutableDial(Node node)
            => node.IsStatic || node.IsTrusted || node.IsBootnode ? PeerAddressPermissions.All : _addressPolicy.AllowedAddresses;

        private bool TryGetEligibleAlternate(Node node, PeerAddressPermissions allowNonRoutable, [NotNullWhen(true)] out Node? alternate)
        {
            if (TryCreateAlternateDialNode(node, PeerAddressPolicy.AllowsNonRoutable(node.Address.Address) ? allowNonRoutable : PeerAddressPermissions.None, out alternate) &&
                IsDialEndpointAcceptable(alternate.Address, allowNonRoutable)) return true;
            alternate = null;
            return false;
        }

        private async Task<bool> TryConnect(Node node, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionAttempt attempt = new();
            Bootstrap clientBootstrap = new();
            clientBootstrap
                .Group(_workerGroup)
                .ChannelFactory(_createClientChannel)
                .Option(ChannelOption.Allocator, NethermindBuffers.RlpxAllocator)
                .Option(ChannelOption.WriteBufferHighWaterMark, (int)3.MB)
                .Option(ChannelOption.WriteBufferLowWaterMark, (int)1.MB)
                .Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default)
                .Option(ChannelOption.ConnectTimeout, _connectTimeout);
            clientBootstrap.Handler(new OutboundChannelInitializer(this, node, attempt));

            Task<IChannel> connectTask;
            // Stoppables stop in parallel, so the peer manager can still dial during Shutdown. ConnectAsync queues the
            // channel registration synchronously, so under the lock a dial either reaches the worker group before
            // Shutdown starts closing it or is skipped.
            lock (_dialLock)
            {
                if (_shutdownCts is null) return false;
                connectTask = clientBootstrap.ConnectAsync(node.Address);
            }

            using CancellationTokenSource delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task firstTask = await Task.WhenAny(connectTask, Task.Delay(_connectTimeout.Add(TimeSpan.FromSeconds(2)), delayCancellation.Token));
            if (firstTask != connectTask)
            {
                bool abandoned = attempt.TryAbandon();
                if (_logger.IsTrace) TraceConnectionTimedOut(node);
                if (!abandoned)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        DisconnectWhenConnected(connectTask);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await connectTask;
                    return true;
                }

                DisconnectWhenConnected(connectTask);
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            delayCancellation.Cancel();
            if (connectTask.IsCanceled)
            {
                if (!attempt.TryAbandon())
                {
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (connectTask.IsFaulted)
            {
                if (!attempt.TryAbandon())
                {
                    return true;
                }

                if (_logger.IsTrace) TraceConnectionFailure(node, connectTask.Exception!);
                return false;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} OUT connected");
            return true;
        }

        private void DisconnectWhenConnected(Task<IChannel> connectTask) =>
            _ = connectTask.ContinueWith(
                _disconnectConnectedChannel,
                null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        internal static bool TryCreateAlternateDialNode(Node node, PeerAddressPermissions allowNonRoutable, [NotNullWhen(true)] out Node? alternate)
        {
            NodeRecord? record = node.Enr;
            AddressFamily alternateFamily = node.Address.AddressFamily switch
            {
                AddressFamily.InterNetwork => AddressFamily.InterNetworkV6,
                AddressFamily.InterNetworkV6 => AddressFamily.InterNetwork,
                _ => AddressFamily.Unknown
            };
            bool familySupported = alternateFamily switch
            {
                AddressFamily.InterNetwork => Socket.OSSupportsIPv4,
                AddressFamily.InterNetworkV6 => Socket.OSSupportsIPv6,
                _ => false
            };
            if (!familySupported || record is null || !node.IsVerifiedEnr(record) ||
                record.EnrSequence < node.HighestObservedEnrSequence ||
                !Node.TryFromEnr(record, alternateFamily, out alternate) ||
                alternate.Id != node.Id || alternate.Address.Equals(node.Address) ||
                (IPAddress.IsLoopback(alternate.Address.Address) && !IPAddress.IsLoopback(node.Address.Address)) ||
                !IsDialAddressAcceptable(alternate.Address.Address, allowNonRoutable))
            {
                alternate = null;
                return false;
            }

            alternate.MergeEnrStateFrom(node);
            alternate.IsStatic = node.IsStatic;
            alternate.IsTrusted = node.IsTrusted;
            alternate.IsBootnode = node.IsBootnode;
            return true;
        }

        internal static bool IsDialAddressAcceptable(IPAddress address, PeerAddressPermissions allowNonRoutable)
            => PeerAddressPolicy.IsAcceptable(address, allowNonRoutable);

        private bool IsDialEndpointAcceptable(IPEndPoint endpoint, PeerAddressPermissions allowNonRoutable)
            => endpoint.Port > 0 && IsDialAddressAcceptable(endpoint.Address, allowNonRoutable) &&
               !PeerAddressPolicy.IsSelfEndpoint(endpoint, _localAddresses.LocalIp, LocalPort) &&
               !PeerAddressPolicy.IsSelfEndpoint(endpoint, _localAddresses.ExternalIp, LocalPort) &&
               (_localAddresses.ExternalIpV4 is not { } ipv4 || !PeerAddressPolicy.IsSelfEndpoint(endpoint, ipv4, LocalPort)) &&
               (_localAddresses.ExternalIpV6 is not { } ipv6 || !PeerAddressPolicy.IsSelfEndpoint(endpoint, ipv6, LocalPort)) &&
               !PeerAddressPolicy.IsSelfEndpoint(endpoint, _listenerState.RlpxAddress ?? _listenerState.PreferredAddress, LocalPort);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceConnectionTimedOut(Node node) =>
            _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} OUT connection timed out");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceConnectionFailure(Node node, Exception exception) =>
            _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} error when OUT connecting {exception:hide}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceRejectedDialEndpoint(Node node, IPEndPoint endpoint) =>
            _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} rejected OUT endpoint {endpoint:hide}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceFilteredDialEndpoint(Node node) =>
            _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} rejected filtered OUT endpoint {node.Address:hide}");

        public event EventHandler<SessionEventArgs> SessionCreated;
        public event SessionDisconnectedEventHandler SessionDisconnected;

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

        private bool ShouldRejectInbound(ISession session, IChannel channel, IPAddress? inboundRemoteIp)
        {
            if (_inboundNodeFilter is null || inboundRemoteIp is null || _privilegedIpProvider.IsPrivileged(inboundRemoteIp))
                return false;

            HashSet<NodeFilter.IpSubnetKey> liveSources = [];
            foreach ((Guid id, SessionActivitySubscription subscription) in _sessionActivitySubscriptions)
            {
                if (id != session.SessionId && subscription.IsOpen && subscription.HasCompletedHandshake && subscription.RemoteIp is { } remoteIp)
                    liveSources.Add(_nodeFilter.GetKey(remoteIp, false));
            }

            if (!liveSources.Contains(_nodeFilter.GetKey(inboundRemoteIp, false)) &&
                _inboundNodeFilter.TryAccept(inboundRemoteIp, liveSources))
                return false;

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Rejecting authenticated inbound connection from filtered IP {inboundRemoteIp}");
            _ = channel.CloseAsync();
            return true;
        }

        private bool TrackChannel(IChannel channel)
        {
            bool closing;
            lock (_activeChannelsLock)
            {
                _activeChannels.TryAdd(channel, 0);
                closing = _closingActiveChannels;
            }

            _ = channel.CloseCompletion.ContinueWith(_untrackChannel, channel, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (!closing)
            {
                return false;
            }

            _ = channel.CloseAsync();
            return true;
        }

        private void UntrackChannel(Task _, object? state) => _activeChannels.TryRemove((IChannel)state!, out byte _);

        private async Task CloseActiveChannelsAsync()
        {
            using ArrayPoolList<IChannel> channels = new(_activeChannels.Count);
            lock (_activeChannelsLock)
            {
                _closingActiveChannels = true;
                foreach (KeyValuePair<IChannel, byte> entry in _activeChannels)
                {
                    channels.Add(entry.Key);
                }
            }

            _activeChannelsClosing.TrySetResult(true);

            Task[] closing = new Task[channels.Count];
            for (int i = 0; i < closing.Length; i++)
            {
                closing[i] = channels[i].CloseAsync();
            }

            try
            {
                await Task.WhenAll(closing).WaitAsync(_shutdownCloseTimeout);
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Debug($"Closing peer channels on shutdown failed: {e.Message}");
            }
        }

        private async Task CloseChannelsAndEventLoopsAsync()
        {
            await CloseActiveChannelsAsync();
            await Task.WhenAll(
                _bossGroup is not null ? _bossGroup.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout) : Task.CompletedTask,
                _workerGroup is not null ? _workerGroup.ShutdownGracefullyAsync(_shutdownCloseTimeout, _shutdownCloseTimeout) : Task.CompletedTask,
                _group.ShutdownGracefullyAsync(_shutdownQuietPeriod, _shutdownCloseTimeout));
        }

        private void InitializeChannel(IChannel channel, Session session, IPAddress inboundRemoteIp)
        {
            if (TrackChannel(channel))
            {
                return;
            }

            Metrics.IncomingConnections++;
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Initializing {session:hide} channel");

            PendingInboundAdmission? admission = TryAdmitInbound(channel, session, inboundRemoteIp);
            if (admission is null)
            {
                _ = channel.CloseAsync();
                return;
            }
            try
            {
                SetTcpSocketOptions(channel);
                ActivateSession(channel, session);
                AddHandshakeHandlers(channel, session, HandshakeRole.Recipient, inboundRemoteIp);
            }
            catch
            {
                admission.Release();
                throw;
            }
        }

        internal PendingInboundAdmission? TryAdmitInbound(IChannel channel, Session session, IPAddress inboundRemoteIp)
        {
            bool privileged = _privilegedIpProvider.IsPrivileged(inboundRemoteIp);
            if (!privileged && _inboundNodeFilter is not null &&
                (_inboundNodeFilter.WasRecentlyAccepted(inboundRemoteIp) || HasLiveConnection(inboundRemoteIp)))
            {
                return null;
            }
            PendingInboundSlots.Source source = PendingInboundSlots.Source.From(inboundRemoteIp);
            if (!_pendingSlots.TryReserve(source, privileged, out bool sourceLimitReached))
            {
                int limit = _maxPendingInboundConnections == 0 ? 0 : privileged ? PendingInboundSlots.PrivilegedLimit : _maxPendingInboundConnections;
                if (_logger.IsTrace) _logger.Trace($"Rejecting inbound connection from {inboundRemoteIp}: pending {(sourceLimitReached ? "source" : "lane")} limit {(sourceLimitReached ? _maxPendingInboundConnectionsPerSource : limit)}, privileged {privileged}");
                return null;
            }

            return new PendingInboundAdmission(_pendingSlots, session, channel, privileged, source);
        }

        internal sealed class PendingInboundAdmission
        {
            private readonly PendingInboundSlots _slots;
            private readonly Session _session;
            private readonly bool _privileged;
            private readonly PendingInboundSlots.Source _source;
            private int _released;

            internal Task ChannelClosed { get; }

            public PendingInboundAdmission(PendingInboundSlots slots, Session session, IChannel channel, bool privileged, PendingInboundSlots.Source source)
            {
                _slots = slots;
                _session = session;
                _privileged = privileged;
                _source = source;
                session.Authenticated += OnAuthenticated;
                session.Disconnected += OnDisconnected;
                ChannelClosed = channel.CloseCompletion.ContinueWith(static (_, state) => ((PendingInboundAdmission)state!).Release(),
                    this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            private void OnAuthenticated(object? sender, EventArgs args) => Release();
            private void OnDisconnected(object? sender, DisconnectEventArgs args) => Release();

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                _session.Authenticated -= OnAuthenticated;
                _session.Disconnected -= OnDisconnected;
                _slots.Release(_source, _privileged);
            }
        }

        private void InitializeOutboundChannel(IChannel channel, ISession session, ConnectionAttempt attempt)
        {
            if (TrackChannel(channel))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Initializing {session:hide} channel");
            SetTcpSocketOptions(channel);
            channel.Pipeline.AddLast("out-session-activation", new OutboundSessionActivationHandler(this, session, attempt));
            AddHandshakeHandlers(channel, session, HandshakeRole.Initiator);
        }

        private void AddHandshakeHandlers(IChannel channel, ISession session, HandshakeRole role, IPAddress? inboundRemoteIp = null)
        {
            NettyHandshakeHandler handshakeHandler = new(_serializationService, _handshakeService, session, role, _logManager, _group, _sendLatency)
            {
                InboundAdmission = role == HandshakeRole.Recipient ? () => !ShouldRejectInbound(session, channel, inboundRemoteIp) : null
            };
            IChannelPipeline pipeline = channel.Pipeline;
            AddTraceLoggingHandler(pipeline, session.Direction.ToString().ToUpper());
            pipeline.AddLast("enc-handshake-dec", new OneTimeLengthFieldBasedFrameDecoder());
            pipeline.AddLast("enc-handshake-handler", handshakeHandler);
        }

        /// <remarks>
        /// The handler stays in the pipeline for the whole connection, and every read, write and flush passes
        /// through it, so it is only added when its TRACE output is enabled.
        /// </remarks>
        internal static void AddTraceLoggingHandler(IChannelPipeline pipeline, string name)
        {
            if (InternalLoggerFactory.GetInstance(name).IsEnabled(InternalLogLevel.TRACE))
            {
                pipeline.AddLast(new LoggingHandler(name, LogLevel.TRACE));
            }
        }

        private void ActivateSession(IChannel channel, ISession session)
        {
            TrackSessionActivity(session);
            _sessionMonitor.AddSession(session);

            _ = channel.CloseCompletion.ContinueWith(
                _onChannelCloseCompleted,
                session,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            SessionCreated?.Invoke(this, new SessionEventArgs(session));
        }

        private void SetTcpSocketOptions(IChannel channel)
        {
            SetChannelOption(channel, ChannelOption.TcpNodelay, true);
            SetChannelOption(channel, ChannelOption.SoKeepalive, true);
        }

        private void SetChannelOption<T>(IChannel channel, ChannelOption<T> option, T value)
        {
            try
            {
                if (!channel.Configuration.SetOption(option, value) && _logger.IsWarn)
                {
                    _logger.Warn($"Failed to set channel option {option}");
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to set channel option {option}: {ex.Message:hide}");
            }
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
            if (state is Session session)
            {
                session.MarkChannelClosed();
            }

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
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session:hide} channel disconnected");
            session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "channel disconnected");
        }

        Task IStoppableService.StopAsync() => Shutdown();

        string IStoppableService.Description => "rlpx host";

        internal int ActiveChannelCount => _activeChannels.Count;

        internal IEventLoopGroup? WorkerGroup => _workerGroup;

        internal IChannel? BootstrapChannel => _bootstrapChannel;

        internal Task ActiveChannelsClosing => _activeChannelsClosing.Task;

        internal bool EventLoopsTerminated =>
            (_bossGroup?.IsTerminated ?? true) && (_workerGroup?.IsTerminated ?? true) && _group.IsTerminated;

        public async Task Shutdown()
        {
            lock (_dialLock)
            {
                CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);
            }

            // Close channels first so Disconnected handlers fire while subscriptions are still active
            await (_bootstrapChannel?.CloseAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(Shutdown)} failed", t.Exception);
                }
            }) ?? Task.CompletedTask);

            if (_logger.IsDebug) _logger.Debug("Closed _bootstrapChannel");

            Task closingTask = CloseChannelsAndEventLoopsAsync();

            TimeSpan warningDelay = _shutdownCloseTimeout * 3 + TimeSpan.FromSeconds(1);
            try
            {
                await closingTask.WaitAsync(warningDelay);
            }
            catch (TimeoutException)
            {
                if (_logger.IsWarn) _logger.Warn($"RLPx event loops have not stopped after {warningDelay.TotalSeconds} seconds. Waiting for them to stop.");
                await closingTask;
            }

            // Detach subscriptions and dispose any sessions that weren't disconnected during shutdown.
            // Sessions whose Disconnected event fired are already disposed via OnDisconnected.
            foreach (KeyValuePair<Guid, SessionActivitySubscription> kvp in _sessionActivitySubscriptions)
            {
                kvp.Value.DisconnectAndDispose();
            }

            _sessionActivitySubscriptions.Clear();

            if (_logger.IsInfo) _logger.Info("Local peer shutdown complete.. please wait for all components to close");
        }

        internal sealed class SessionActivitySubscription
        {
            private readonly RlpxHost _rlpxHost;
            private readonly ISession _session;
            private readonly Session? _concreteSession;
            private readonly EventHandler<DisconnectEventArgs>? _onDisconnected;
            private readonly EventHandler<PeerEventArgs>? _refreshNodeFilter;
            internal IPAddress? RemoteIp { get; }
            internal bool HasCompletedHandshake => _session.State is SessionState.HandshakeComplete or SessionState.Initialized;
            internal bool OwnsSource => _session.Direction != ConnectionDirection.In || HasCompletedHandshake;
            internal bool IsOpen => !_session.IsClosing && !_session.IsChannelClosed;

            public SessionActivitySubscription(RlpxHost rlpxHost, ISession session)
            {
                _rlpxHost = rlpxHost;
                _session = session;
                RemoteIp = IPAddress.TryParse(session.RemoteHost, out IPAddress? remoteIp) ? remoteIp : null;
                _concreteSession = session as Session;
                if (_concreteSession is null)
                {
                    _refreshNodeFilter = RefreshNodeFilter;
                    _onDisconnected = OnDisconnected;
                }
            }

            public void Attach()
            {
                if (_concreteSession is not null)
                {
                    _concreteSession.SetActivityObserver(_rlpxHost);
                    return;
                }

                _session.MsgReceived += _refreshNodeFilter!;
                _session.MsgDelivered += _refreshNodeFilter!;
                _session.Disconnected += _onDisconnected!;
            }

            public void Detach()
            {
                DetachSession();
                _rlpxHost._sessionActivitySubscriptions.TryRemove(_session.SessionId, out _);
            }

            public void DetachSession()
            {
                if (_concreteSession is not null)
                {
                    _concreteSession.SetActivityObserver(null);
                }
                else
                {
                    _session.MsgReceived -= _refreshNodeFilter!;
                    _session.MsgDelivered -= _refreshNodeFilter!;
                    _session.Disconnected -= _onDisconnected!;
                }
            }

            public void DisconnectAndDispose()
            {
                try
                {
                    _session.MarkDisconnected(DisconnectReason.AppClosing, DisconnectType.Local, "shutdown");
                }
                catch (InvalidOperationException)
                {
                    // Session may already be disposed or in a state that doesn't allow disposal
                }
            }

            private void RefreshNodeFilter(object? _, PeerEventArgs __)
            {
                Node remoteNode = _session.Node;
                _rlpxHost.RefreshNodeFilter(_session, remoteNode);
            }

            private void OnDisconnected(object? _, DisconnectEventArgs args) => _rlpxHost.OnSessionDisconnected(_session, args);
        }

        void ISessionActivityObserver.OnSessionActivity(Session session)
        {
            Node remoteNode = session.Node;
            RefreshNodeFilter(session, remoteNode);
        }

        private void RefreshNodeFilter(ISession session, Node node)
        {
            if (session.Direction == ConnectionDirection.In)
                _inboundNodeFilter?.Touch(node.Address.Address);
            else
                _nodeFilter.Touch(node.Address.Address, node.IsStatic || node.IsBootnode);
        }

        void ISessionActivityObserver.OnSessionDisconnected(Session session, DisconnectEventArgs args) =>
            OnSessionDisconnected(session, args);

        private void OnSessionDisconnected(ISession session, DisconnectEventArgs args)
        {
            if (!_sessionActivitySubscriptions.TryRemove(session.SessionId, out SessionActivitySubscription? subscription))
            {
                return;
            }

            subscription.DetachSession();
            _sessionMonitor.RemoveSession(session);
            try
            {
                SessionDisconnected?.Invoke(this, session, args);
            }
            finally
            {
                session.Dispose();
            }
        }

        private sealed class InboundChannelInitializer(RlpxHost rlpxHost) : ChannelInitializer<IChannel>
        {
            private readonly RlpxHost _rlpxHost = rlpxHost;

            protected override void InitChannel(IChannel channel)
            {
                Session session = new(_rlpxHost.LocalPort, channel, _rlpxHost._disconnectsAnalyzer, _rlpxHost._logManager);
                IPEndPoint? ipEndPoint = channel.RemoteAddress.ToIPEndpoint();
                IPAddress remoteIp = ipEndPoint.Address.NormalizeMappedIPv4();
                session.RemoteHost = remoteIp.ToString();
                session.RemotePort = ipEndPoint.Port;
                _rlpxHost.InitializeChannel(channel, session, remoteIp);
            }
        }

        private sealed class OutboundChannelInitializer(RlpxHost rlpxHost, Node node, ConnectionAttempt attempt) : ChannelInitializer<IChannel>
        {
            private readonly RlpxHost _rlpxHost = rlpxHost;
            private readonly Node _node = node;
            private readonly ConnectionAttempt _attempt = attempt;

            protected override void InitChannel(IChannel channel)
            {
                Session session = new(_rlpxHost.LocalPort, _node, channel, _rlpxHost._disconnectsAnalyzer, _rlpxHost._logManager);
                _rlpxHost.InitializeOutboundChannel(channel, session, _attempt);
            }
        }

        private sealed class ConnectionAttempt
        {
            private int _state;

            public bool TryActivate() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

            public bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
        }

        private sealed class OutboundSessionActivationHandler(RlpxHost rlpxHost, ISession session, ConnectionAttempt attempt) : ChannelHandlerAdapter
        {
            private readonly RlpxHost _rlpxHost = rlpxHost;
            private readonly ISession _session = session;
            private readonly ConnectionAttempt _attempt = attempt;

            public override void ChannelActive(IChannelHandlerContext context)
            {
                if (_attempt.TryActivate())
                {
                    Metrics.OutgoingConnections++;
                    _rlpxHost.ActivateSession(context.Channel, _session);
                }
                else
                {
                    _ = context.CloseAsync();
                    return;
                }

                base.ChannelActive(context);
            }

            public override Task ConnectAsync(IChannelHandlerContext context, EndPoint remoteAddress, EndPoint localAddress)
            {
                Task connect = context.ConnectAsync(remoteAddress, localAddress);
                _ = connect.ContinueWith(CloseChannel, context.Channel, CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return connect;
            }

            private static void CloseChannel(Task _, object? state) => _ = ((IChannel)state!).CloseAsync();
        }

        public ISessionMonitor SessionMonitor => _sessionMonitor;
    }
}
