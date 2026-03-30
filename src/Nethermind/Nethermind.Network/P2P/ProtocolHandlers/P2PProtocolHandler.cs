// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class P2PProtocolHandler(
    ISession session,
    PublicKey localNodeId,
    INodeStatsManager nodeStatsManager,
    IMessageSerializationService serializer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ILogManager logManager)
    : ProtocolHandlerBase(session, nodeStatsManager, serializer, backgroundTaskScheduler, logManager), IPingSender, IP2PProtocolHandler
{
    private const int MaxCapabilityCount = 64;
    /// <summary>
    /// Maximum size of a base protocol (p2p) message in bytes (2 KiB).
    /// </summary>
    public static readonly long BaseProtocolMaxMsgSize = 2.KiB;

    private TaskCompletionSource<Packet> _pongCompletionSource;
    private readonly INodeStatsManager _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
    private bool _sentHello;
    private readonly List<Capability> _agreedCapabilities = new();
    private List<Capability> _availableCapabilities = new();

    private byte _protocolVersion = 5;

    public override byte ProtocolVersion => _protocolVersion;

    public override string ProtocolCode => Protocol.P2P;

    public override int MessageIdSpaceSize => 0x10;

    protected override TimeSpan InitTimeout => Timeouts.P2PHello;

    public IReadOnlyList<Capability> AgreedCapabilities { get { return _agreedCapabilities; } }
    public IReadOnlyList<Capability> AvailableCapabilities { get { return _availableCapabilities; } }
    private readonly List<Capability> _supportedCapabilities = new();

    public int ListenPort { get; } = session.LocalPort;
    public PublicKey LocalNodeId { get; } = localNodeId;
    private string RemoteClientId { get; set; }

    public override event EventHandler<ProtocolInitializedEventArgs>? ProtocolInitialized;

    public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested;

    public bool HasAvailableCapability(Capability capability) => _availableCapabilities.Contains(capability);
    public bool HasAgreedCapability(Capability capability) => _agreedCapabilities.Contains(capability);
    public void AddSupportedCapability(Capability capability)
    {
        if (_supportedCapabilities.Contains(capability))
        {
            return;
        }

        _supportedCapabilities.Add(capability);
    }

    public override void RegisterWith(ISession session, IProtocolRegistrar registrar) => registrar.Register(session, this);

    public override void Init()
    {
        SendHello();

        // We are expecting to receive Hello message anytime from the handshake completion,
        // irrespective of sending Hello from our side
        _ = CheckProtocolInitTimeout();
    }

    public override void HandleMessage(Packet msg)
    {
        int size = msg.Data.Length;

        if (size > BaseProtocolMaxMsgSize)
        {
            DisconnectMessageTooLarge(size);
            return;
        }

        switch (msg.PacketType)
        {
            case P2PMessageCode.Hello:
                {
                    using HelloMessage helloMessage = Deserialize<HelloMessage>(msg.Data);
                    HandleHello(helloMessage);
                    ReportIn(helloMessage, size);

                    // We need to initialize subprotocols in alphabetical order. Protocols are using AdaptiveId,
                    // which should be constant for the whole session. Some protocols (like Eth) are sending messages
                    // on initialization and we need to avoid changing theirs AdaptiveId by initializing protocols,
                    // which are alphabetically before already initialized ones.
                    foreach (Capability capability in
                        _agreedCapabilities.GroupBy(static c => c.ProtocolCode).Select(static c => c.OrderBy(static v => v.Version).Last()).OrderBy(static c => c.ProtocolCode))
                    {
                        if (Logger.IsTrace) TraceStartingProtocolHandler(capability);
                        SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                    }

                    break;
                }
            case P2PMessageCode.Disconnect:
                {
                    using DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                    ReportIn(disconnectMessage, size);

                    EthDisconnectReason disconnectReason =
                        FastEnum.IsDefined((EthDisconnectReason)disconnectMessage.Reason)
                            ? (EthDisconnectReason)disconnectMessage.Reason
                            : EthDisconnectReason.Other;

                    if (Logger.IsTrace) TraceDisconnect(disconnectMessage.Reason, disconnectReason);

                    Close(disconnectReason);
                    break;
                }
            case P2PMessageCode.Ping:
                {
                    if (Logger.IsTrace) TracePing();
                    HandlePing();
                    ReportIn("Ping", size);
                    break;
                }
            case P2PMessageCode.Pong:
                {
                    if (Logger.IsTrace) TracePong();
                    HandlePong(msg);
                    ReportIn("Pong", size);
                    break;
                }
            case P2PMessageCode.AddCapability:
                {
                    using AddCapabilityMessage message = Deserialize<AddCapabilityMessage>(msg.Data);
                    Capability capability = message.Capability;
                    if (_availableCapabilities.Contains(capability))
                    {
                        if (Logger.IsTrace) TraceDuplicateCapability(capability);
                        break;
                    }

                    if (_availableCapabilities.Count >= MaxCapabilityCount)
                    {
                        DisconnectTooManyCapabilities();
                        break;
                    }

                    _availableCapabilities.Add(capability);
                    if (!_supportedCapabilities.Contains(capability))
                    {
                        if (Logger.IsTrace) TraceUnsupportedCapability(capability);
                        break;
                    }

                    _agreedCapabilities.Add(capability);
                    if (Logger.IsTrace) TraceStartingHandler(capability);
                    SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                    break;
                }
            default:
                if (Logger.IsTrace) TraceUnhandledPacket(msg.PacketType);
                break;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DisconnectMessageTooLarge(int size)
            => Session.InitiateDisconnect(DisconnectReason.MessageLimitsBreached, $"P2P message too large: {size} bytes, max {BaseProtocolMaxMsgSize} bytes");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceStartingProtocolHandler(Capability capability)
            => Logger.Trace($"{Session} Starting protocolHandler for {capability.ProtocolCode} v{capability.Version} on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceDisconnect(int reason, EthDisconnectReason disconnectReason)
            => Logger.Trace(!FastEnum.IsDefined((EthDisconnectReason)reason)
                ? $"{Session} unknown disconnect reason ({reason}) on {Session.RemotePort}"
                : $"{Session} Received disconnect ({disconnectReason}) on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TracePing()
            => Logger.Trace($"{Session} Received PING on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TracePong()
            => Logger.Trace($"{Session} Received PONG on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceDuplicateCapability(Capability capability)
            => Logger.Trace($"{Session.RemoteNodeId} duplicate capability {capability} ignored on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DisconnectTooManyCapabilities()
            => Session.InitiateDisconnect(DisconnectReason.MessageLimitsBreached, $"Too many capabilities advertised: {_availableCapabilities.Count + 1}, max {MaxCapabilityCount}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceUnsupportedCapability(Capability capability)
            => Logger.Trace($"{Session.RemoteNodeId} advertised unsupported capability {capability} on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceStartingHandler(Capability capability)
            => Logger.Trace($"{Session.RemoteNodeId} Starting handler for {capability} on {Session.RemotePort}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceUnhandledPacket(int packetType)
            => Logger.Trace($"{Session.RemoteNodeId} Unhandled packet type: {packetType}");
    }

    private void HandleHello(HelloMessage hello)
    {
        bool isInbound = !_sentHello;

        if (Logger.IsTrace) TraceReceivedHello();

        if (!hello.NodeId.Equals(Session.RemoteNodeId))
        {
            if (Logger.IsDebug) DebugInconsistentNodeId(hello, isInbound);
            // it does not really matter if there is mismatch - we do not use it anywhere
        }

        RemoteClientId = hello.ClientId;
        Session.Node.ClientId = hello.ClientId;

        if (Logger.IsTrace) TraceInitiating(hello);

        // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
        // Clients implementing a newer version simply send a packet with higher version and possibly additional list elements.
        // * If such a packet is received by a node with lower version,
        //   it will blindly assume that the remote end is backwards-compatible and respond with the old handshake.
        // * If the packet is received by a node with equal version,
        //   new features of the protocol can be used.
        // * If the packet is received by a node with higher version,
        //   it can enable backwards-compatibility logic or drop the connection.

        _protocolVersion = hello.P2PVersion;

        IOwnedReadOnlyList<Capability>? capabilities = hello.Capabilities;
        _availableCapabilities = new List<Capability>(capabilities);
        foreach (Capability theirCapability in capabilities)
        {
            if (_supportedCapabilities.Contains(theirCapability))
            {
                if (Logger.IsTrace) TraceAgreedCapability(theirCapability);
                _agreedCapabilities.Add(theirCapability);
            }
            else
            {
                if (Logger.IsTrace) TraceCapabilityNotSupported(theirCapability);
            }
        }

        if (_agreedCapabilities.Count == 0)
        {
            _nodeStatsManager.ReportFailedValidation(Session.Node, CompatibilityValidationType.Capabilities);
            DisconnectNoCapabilityMatched(capabilities);
        }

        ReceivedProtocolInitMsg(hello);

        P2PProtocolInitializedEventArgs eventArgs = new(this)
        {
            P2PVersion = ProtocolVersion,
            ClientId = RemoteClientId,
            Capabilities = _availableCapabilities,
            ListenPort = hello.ListenPort
        };

        ProtocolInitialized?.Invoke(this, eventArgs);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceReceivedHello()
            => Logger.Trace($"{Session} P2P received hello.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DebugInconsistentNodeId(HelloMessage hello, bool isInbound)
            => Logger.Debug($"Inconsistent Node ID details - expected {Session.RemoteNodeId}, " +
                            $"received hello with {hello.NodeId} " +
                            $"on {(isInbound ? "IN connection" : "OUT connection")}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceInitiating(HelloMessage hello)
            => Logger.Trace(!_sentHello
                ? $"{Session.RemoteNodeId} P2P initiating inbound {hello.Protocol}.{hello.P2PVersion} on {hello.ListenPort} ({hello.ClientId})"
                : $"{Session.RemoteNodeId} P2P initiating outbound {hello.Protocol}.{hello.P2PVersion} on {hello.ListenPort} ({hello.ClientId})");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceAgreedCapability(Capability capability)
            => Logger.Trace($"{Session.RemoteNodeId} Agreed on {capability.ProtocolCode} v{capability.Version}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCapabilityNotSupported(Capability capability)
            => Logger.Trace($"{Session.RemoteNodeId} Capability not supported {capability.ProtocolCode} v{capability.Version}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DisconnectNoCapabilityMatched(IOwnedReadOnlyList<Capability> capabilities)
            => Session.InitiateDisconnect(DisconnectReason.NoCapabilityMatched, $"capabilities: {string.Join(", ", capabilities)}");
    }

    public async Task<bool> SendPing()
    {
        TaskCompletionSource<Packet> newSource = new();
        TaskCompletionSource<Packet> previousSource =
            Interlocked.CompareExchange(ref _pongCompletionSource, newSource, null);

        if (previousSource is not null)
        {
            if (Logger.IsWarn) WarnDuplicatePing();
            return true;
        }

        Task<Packet> pongTask = newSource.Task;

        if (Logger.IsTrace) TraceSendingPing();

        Send(PingMessage.Instance);

        _nodeStatsManager.ReportEvent(Session.Node, NodeStatsEventType.P2PPingOut);
        long startTime = Stopwatch.GetTimestamp();

        using CancellationTokenSource delayCancellation = new();
        try
        {
            Task firstTask = await Task.WhenAny(pongTask, Task.Delay(Timeouts.P2PPing, delayCancellation.Token));
            if (firstTask != pongTask)
            {
                _nodeStatsManager.ReportTransferSpeedEvent(
                    Session.Node,
                    TransferSpeedType.Latency,
                    (long)Timeouts.P2PPing.TotalMilliseconds);
                return false;
            }

            long latency = (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
            _nodeStatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Latency, latency);
            return true;
        }
        finally
        {
            delayCancellation.Cancel();
            Interlocked.CompareExchange(ref _pongCompletionSource, null, newSource);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnDuplicatePing()
            => Logger.Warn($"Another ping request in process: {Session.Node:c}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceSendingPing()
            => Logger.Trace($"{Session} P2P sending ping on {Session.RemotePort} ({RemoteClientId})");
    }

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
    {
        if (Logger.IsTrace) TraceSendingDisconnect(disconnectReason, details);
        if (NetworkDiagTracer.IsEnabled) ReportDisconnect(disconnectReason, details);

        DisconnectMessage message = new(disconnectReason.ToEthDisconnectReason());
        Send(message);
        Dispose();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceSendingDisconnect(DisconnectReason reason, string details)
            => Logger.Trace($"Sending disconnect {reason} ({details}) to {Session.Node:s}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReportDisconnect(DisconnectReason reason, string details)
            => NetworkDiagTracer.ReportDisconnect(Session.Node.Address, $"Local {reason} {details}");
    }

    private void SendHello()
    {
        if (Logger.IsTrace) TraceSendingHello();

        HelloMessage helloMessage = new()
        {
            Capabilities = _supportedCapabilities.ToPooledList(),
            ClientId = ProductInfo.PublicClientId,
            NodeId = LocalNodeId,
            ListenPort = ListenPort,
            P2PVersion = ProtocolVersion
        };

        _sentHello = true;
        Send(helloMessage);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceSendingHello()
            => Logger.Trace($"{Session} {Name} sending hello with Client ID {ProductInfo.PublicClientId}, protocol {Name}, listen port {ListenPort}");
    }

    private void HandlePing()
    {
        if (Logger.IsTrace) TraceRespondingToPing();
        Send(PongMessage.Instance);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceRespondingToPing()
            => Logger.Trace($"{Session} P2P responding to ping");
    }

    private void Close(EthDisconnectReason ethDisconnectReason)
    {
        Dispose();
        if (ethDisconnectReason != EthDisconnectReason.TooManyPeers &&
            ethDisconnectReason != EthDisconnectReason.Other &&
            ethDisconnectReason != EthDisconnectReason.DisconnectRequested)
        {
            if (Logger.IsDebug) DebugReceivedDisconnect(ethDisconnectReason);
        }
        else
        {
            if (Logger.IsTrace) TraceReceivedDisconnect(ethDisconnectReason);
        }

        // Received disconnect message, triggering direct TCP disconnection
        Session.MarkDisconnected(ethDisconnectReason.ToDisconnectReason(), DisconnectType.Remote, "message");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DebugReceivedDisconnect(EthDisconnectReason reason)
            => Logger.Debug($"{Session} received disconnect [{reason}]");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceReceivedDisconnect(EthDisconnectReason reason)
            => Logger.Trace($"{Session} P2P received disconnect [{reason}]");
    }

    public override string Name => Protocol.P2P;

    private void HandlePong(Packet msg)
    {
        if (Logger.IsTrace) TraceHandlingPong();
        _nodeStatsManager.ReportEvent(Session.Node, NodeStatsEventType.P2PPingIn);
        _pongCompletionSource?.TrySetResult(msg);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceHandlingPong()
            => Logger.Trace($"{Session} sending P2P pong");
    }

    public override void Dispose()
    {
        // Clear Events if set
        ProtocolInitialized = null;
        SubprotocolRequested = null;
    }

    public IReadOnlyList<Capability> GetCapabilities() =>
        _agreedCapabilities.Count > 0 ? _agreedCapabilities : _supportedCapabilities;
}
