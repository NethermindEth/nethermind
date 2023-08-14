// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class P2PProtocolHandler : ProtocolHandlerBase, IPingSender, IP2PProtocolHandler
{
    private TaskCompletionSource<Packet> _pongCompletionSource;
    private readonly INodeStatsManager _nodeStatsManager;
    private bool _sentHello;
    private List<Capability> _agreedCapabilities { get; }
    private List<Capability> _availableCapabilities { get; set; }

    private byte _protocolVersion = 5;

    public override byte ProtocolVersion => _protocolVersion;

    public override string ProtocolCode => Protocol.P2P;

    public override int MessageIdSpaceSize => 0x10;

    protected override TimeSpan InitTimeout => Timeouts.P2PHello;

    public static readonly IEnumerable<Capability> DefaultCapabilities = new Capability[]
    {
        new(Protocol.Eth, 66)
    };

    public IReadOnlyList<Capability> AgreedCapabilities { get { return _agreedCapabilities; } }
    public IReadOnlyList<Capability> AvailableCapabilities { get { return _availableCapabilities; } }
    private readonly List<Capability> SupportedCapabilities = DefaultCapabilities.ToList();
    private readonly Regex? _clientIdPattern;

    public int ListenPort { get; }
    public PublicKey LocalNodeId { get; }
    public string RemoteClientId { get; private set; }

    public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

    public override event EventHandler<ProtocolEventArgs> SubprotocolRequested;

    public P2PProtocolHandler(
        ISession session,
        PublicKey localNodeId,
        INodeStatsManager nodeStatsManager,
        IMessageSerializationService serializer,
        Regex? clientIdPattern,
        ILogManager logManager) : base(session, nodeStatsManager, serializer, logManager)
    {
        _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
        _clientIdPattern = clientIdPattern;

        LocalNodeId = localNodeId;
        ListenPort = session.LocalPort;
        _agreedCapabilities = new List<Capability>();
        _availableCapabilities = new List<Capability>();
    }

    public bool HasAvailableCapability(Capability capability) => _availableCapabilities.Contains(capability);
    public bool HasAgreedCapability(Capability capability) => _agreedCapabilities.Contains(capability);
    public void AddSupportedCapability(Capability capability)
    {
        if (SupportedCapabilities.Contains(capability))
        {
            return;
        }

        SupportedCapabilities.Add(capability);
    }

    public override void Init()
    {
        SendHello();

        // We are expecting to receive Hello message anytime from the handshake completion,
        // irrespective of sending Hello from our side
        CheckProtocolInitTimeout().ContinueWith(x =>
        {
            if (x.IsFaulted && Logger.IsError)
            {
                Logger.Error("Error during p2pProtocol handler timeout logic", x.Exception);
            }
        });
    }

    public override void HandleMessage(Packet msg)
    {
        int size = msg.Data.Length;

        switch (msg.PacketType)
        {
            case P2PMessageCode.Hello:
                {
                    Metrics.HellosReceived++;
                    HelloMessage helloMessage = Deserialize<HelloMessage>(msg.Data);
                    HandleHello(helloMessage);
                    ReportIn(helloMessage, size);

                    // We need to initialize subprotocols in alphabetical order. Protocols are using AdaptiveId,
                    // which should be constant for the whole session. Some protocols (like Eth) are sending messages
                    // on initialization and we need to avoid changing theirs AdaptiveId by initializing protocols,
                    // which are alphabetically before already initialized ones.
                    foreach (Capability capability in
                        _agreedCapabilities.GroupBy(c => c.ProtocolCode).Select(c => c.OrderBy(v => v.Version).Last()).OrderBy(c => c.ProtocolCode))
                    {
                        if (Logger.IsTrace) Logger.Trace($"{Session} Starting protocolHandler for {capability.ProtocolCode} v{capability.Version} on {Session.RemotePort}");
                        SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                    }

                    break;
                }
            case P2PMessageCode.Disconnect:
                {
                    DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                    ReportIn(disconnectMessage, size);

                    EthDisconnectReason disconnectReason =
                        FastEnum.IsDefined<EthDisconnectReason>((byte)disconnectMessage.Reason)
                            ? ((EthDisconnectReason)disconnectMessage.Reason)
                            : EthDisconnectReason.Other;

                    if (Logger.IsTrace)
                    {
                        if (!FastEnum.IsDefined<EthDisconnectReason>((byte)disconnectMessage.Reason))
                        {
                            Logger.Trace($"{Session} unknown disconnect reason ({disconnectMessage.Reason}) on {Session.RemotePort}");
                        }
                        else
                        {
                            Logger.Trace($"{Session} Received disconnect ({disconnectReason}) on {Session.RemotePort}");
                        }
                    }

                    Close(disconnectReason);
                    break;
                }
            case P2PMessageCode.Ping:
                {
                    if (Logger.IsTrace) Logger.Trace($"{Session} Received PING on {Session.RemotePort}");
                    HandlePing();
                    ReportIn("Ping", size);
                    break;
                }
            case P2PMessageCode.Pong:
                {
                    if (Logger.IsTrace) Logger.Trace($"{Session} Received PONG on {Session.RemotePort}");
                    HandlePong(msg);
                    ReportIn("Pong", size);
                    break;
                }
            case P2PMessageCode.AddCapability:
                {
                    AddCapabilityMessage message = Deserialize<AddCapabilityMessage>(msg.Data);
                    Capability capability = message.Capability;
                    _agreedCapabilities.Add(message.Capability);
                    SupportedCapabilities.Add(message.Capability);
                    if (Logger.IsTrace)
                        Logger.Trace($"{Session.RemoteNodeId} Starting handler for {capability} on {Session.RemotePort}");
                    SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                    break;
                }
            default:
                if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Unhandled packet type: {msg.PacketType}");
                break;
        }
    }

    private void HandleHello(HelloMessage hello)
    {
        bool isInbound = !_sentHello;

        if (Logger.IsTrace) Logger.Trace($"{Session} P2P received hello.");

        if (!hello.NodeId.Equals(Session.RemoteNodeId))
        {
            if (Logger.IsDebug)
                Logger.Debug($"Inconsistent Node ID details - expected {Session.RemoteNodeId}, " +
                             $"received hello with {hello.NodeId} " +
                             $"on {(isInbound ? "IN connection" : "OUT connection")}");
            // it does not really matter if there is mismatch - we do not use it anywhere
            //                throw new NodeDetailsMismatchException();
        }

        RemoteClientId = hello.ClientId;
        Session.Node.ClientId = hello.ClientId;

        if (Logger.IsTrace) Logger.Trace(!_sentHello
            ? $"{Session.RemoteNodeId} P2P initiating inbound {hello.Protocol}.{hello.P2PVersion} " +
              $"on {hello.ListenPort} ({hello.ClientId})"
            : $"{Session.RemoteNodeId} P2P initiating outbound {hello.Protocol}.{hello.P2PVersion} " +
              $"on {hello.ListenPort} ({hello.ClientId})");

        // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
        // Clients implementing a newer version simply send a packet with higher version and possibly additional list elements.
        // * If such a packet is received by a node with lower version,
        //   it will blindly assume that the remote end is backwards-compatible and respond with the old handshake.
        // * If the packet is received by a node with equal version,
        //   new features of the protocol can be used.
        // * If the packet is received by a node with higher version,
        //   it can enable backwards-compatibility logic or drop the connection.

        _protocolVersion = hello.P2PVersion;

        List<Capability> capabilities = hello.Capabilities;
        _availableCapabilities = new List<Capability>(capabilities);
        foreach (Capability theirCapability in capabilities)
        {
            if (SupportedCapabilities.Contains(theirCapability))
            {
                if (Logger.IsTrace)
                    Logger.Trace($"{Session.RemoteNodeId} Agreed on {theirCapability.ProtocolCode} v{theirCapability.Version}");
                _agreedCapabilities.Add(theirCapability);
            }
            else
            {
                if (Logger.IsTrace)
                    Logger.Trace($"{Session.RemoteNodeId} Capability not supported " +
                                 $"{theirCapability.ProtocolCode} v{theirCapability.Version}");
            }
        }

        if (_agreedCapabilities.Count == 0)
        {
            _nodeStatsManager.ReportFailedValidation(Session.Node, CompatibilityValidationType.Capabilities);
            Session.InitiateDisconnect(
                DisconnectReason.NoCapabilityMatched,
                $"capabilities: {string.Join(", ", capabilities)}");
        }

        if (_clientIdPattern?.IsMatch(hello.ClientId) == false)
        {
            Session.InitiateDisconnect(
                DisconnectReason.ClientFiltered,
                $"clientId: {hello.ClientId}");
        }

        ReceivedProtocolInitMsg(hello);

        P2PProtocolInitializedEventArgs eventArgs = new(this)
        {
            P2PVersion = ProtocolVersion,
            ClientId = RemoteClientId,
            Capabilities = capabilities,
            ListenPort = hello.ListenPort
        };

        ProtocolInitialized?.Invoke(this, eventArgs);
    }

    [SuppressMessage("ReSharper", "HeuristicUnreachableCode")]
    public async Task<bool> SendPing()
    {
        // ReSharper disable once AssignNullToNotNullAttribute
        TaskCompletionSource<Packet> previousSource =
            Interlocked.CompareExchange(ref _pongCompletionSource, new TaskCompletionSource<Packet>(), null);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (previousSource is not null)
        {
            if (Logger.IsWarn) Logger.Warn($"Another ping request in process: {Session.Node:c}");
            return true;
        }

        Task<Packet> pongTask = _pongCompletionSource.Task;

        if (Logger.IsTrace) Logger.Trace($"{Session} P2P sending ping on {Session.RemotePort} ({RemoteClientId})");
        Send(PingMessage.Instance);
        _nodeStatsManager.ReportEvent(Session.Node, NodeStatsEventType.P2PPingOut);
        Stopwatch stopwatch = Stopwatch.StartNew();

        CancellationTokenSource delayCancellation = new();
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

            long latency = stopwatch.ElapsedMilliseconds;
            _nodeStatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Latency, latency);
            return true;
        }
        finally
        {
            delayCancellation?.Cancel(); // do not remove ? -> ReSharper issue
            _pongCompletionSource = null;
        }
    }

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
    {
        if (Logger.IsTrace)
            Logger.Trace($"Sending disconnect {disconnectReason} ({details}) to {Session.Node:s}");
        DisconnectMessage message = new(disconnectReason.ToEthDisconnectReason());
        Send(message);
        if (NetworkDiagTracer.IsEnabled)
            NetworkDiagTracer.ReportDisconnect(Session.Node.Address, $"Local {disconnectReason} {details}");
    }

    private void SendHello()
    {
        if (Logger.IsTrace)
        {
            Logger.Trace($"{Session} {Name} sending hello with Client ID {ProductInfo.ClientId}, " +
                         $"protocol {Name}, listen port {ListenPort}");
        }

        HelloMessage helloMessage = new()
        {
            Capabilities = SupportedCapabilities,
            ClientId = ProductInfo.ClientId,
            NodeId = LocalNodeId,
            ListenPort = ListenPort,
            P2PVersion = ProtocolVersion
        };

        _sentHello = true;
        Send(helloMessage);
        Metrics.HellosSent++;
    }

    private void HandlePing()
    {
        if (Logger.IsTrace) Logger.Trace($"{Session} P2P responding to ping");
        Send(PongMessage.Instance);
    }

    private void Close(EthDisconnectReason ethDisconnectReason)
    {
        if (ethDisconnectReason != EthDisconnectReason.TooManyPeers &&
            ethDisconnectReason != EthDisconnectReason.Other &&
            ethDisconnectReason != EthDisconnectReason.DisconnectRequested)
        {
            if (Logger.IsDebug) Logger.Debug($"{Session} received disconnect [{ethDisconnectReason}]");
        }
        else
        {
            if (Logger.IsTrace) Logger.Trace($"{Session} P2P received disconnect [{ethDisconnectReason}]");
        }

        // Received disconnect message, triggering direct TCP disconnection
        Session.MarkDisconnected(ethDisconnectReason.ToDisconnectReason(), DisconnectType.Remote, "message");
    }

    public override string Name => Protocol.P2P;

    private void HandlePong(Packet msg)
    {
        if (Logger.IsTrace) Logger.Trace($"{Session} sending P2P pong");
        _nodeStatsManager.ReportEvent(Session.Node, NodeStatsEventType.P2PPingIn);
        _pongCompletionSource?.TrySetResult(msg);
    }

    public override void Dispose()
    {
    }
}
