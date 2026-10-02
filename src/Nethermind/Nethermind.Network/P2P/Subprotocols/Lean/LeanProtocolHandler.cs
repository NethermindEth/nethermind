// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Eip8288;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Negotiated proof-wrapper gossip for the EIP-8288 prototype.</summary>
public sealed class LeanProtocolHandler : ZeroProtocolHandlerBase, IStaticProtocolInfo
{
    public static string Code => "lean";
    public static byte Version => 1;
    public override string Name => "lean1";
    public override string ProtocolCode => Code;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => 2;
    protected override TimeSpan InitTimeout => TimeSpan.FromSeconds(10);
    private readonly IBlockTree _blockTree;
    private readonly ProofWrapperService _wrappers;
    private readonly LeanProofGossip _gossip;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private int _receiving;
    private bool _initialized;
    private int _disposed;

    public LeanProtocolHandler(ISession session, INodeStatsManager nodeStats, IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler, ILogManager logManager,
        IBlockTree blockTree, ProofWrapperService wrappers, LeanProofGossip gossip)
        : base(session, nodeStats, serializer, backgroundTaskScheduler, logManager)
    {
        _blockTree = blockTree;
        _wrappers = wrappers;
        _gossip = gossip;
        _stopToken = _stop.Token;
    }

    public override void Init()
    {
        if (!Session.HasAgreedCapability(new Capability(Code, Version)) || !_wrappers.IsEnabled || _blockTree.Genesis?.Hash is not { } genesis)
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean prototype fork unavailable");
            return;
        }
        Send(new LeanStatusMessage(_blockTree.ChainId, genesis, Eip8288Constants.AggregatedVk.ToArray()));
        _ = CheckProtocolInitTimeout();
    }

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Session.IsClosing) return true;
        if (message.PacketType == 0)
        {
            LeanStatusMessage status = Deserialize<LeanStatusMessage>(message.Content);
            if (_initialized || !Session.HasAgreedCapability(new Capability(Code, Version)) || !_wrappers.IsEnabled || status.ChainId != _blockTree.ChainId
                || status.GenesisHash != _blockTree.Genesis?.Hash || !status.VerificationKey.AsSpan().SequenceEqual(Eip8288Constants.AggregatedVk))
            {
                Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean chain or guest mismatch");
                return true;
            }
            _initialized = true;
            ReceivedProtocolInitMsg(status);
            _gossip.AddPeer(Broadcast);
            NotifyProtocolInitialized(new ProtocolInitializedEventArgs(this));
            return true;
        }
        if (message.PacketType != 1) return false;
        if (!_initialized || Volatile.Read(ref _disposed) != 0 || !_wrappers.IsEnabled)
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean wrapper before handshake");
            return true;
        }
        if (Interlocked.CompareExchange(ref _receiving, 1, 0) != 0)
        {
            Session.InitiateDisconnect(DisconnectReason.MessageLimitsBreached, "Too many pending lean wrappers");
            return true;
        }
        try
        {
            LeanProofWrapperMessage wrapper = Deserialize<LeanProofWrapperMessage>(message.Content);
            if (!BackgroundTaskScheduler.TryScheduleBackgroundTask(wrapper, Receive)) Interlocked.Exchange(ref _receiving, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _receiving, 0);
            throw;
        }
        return true;
    }

    private async ValueTask Receive(LeanProofWrapperMessage message, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, cancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            Result<Hash256[]> result = await _wrappers.AcceptAsync(message.Wrapper, linked.Token);
            if (!result.IsSuccess) throw new RlpException(result.Error ?? "Invalid lean proof wrapper");
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref _receiving, 0);
        }
    }

    private void Broadcast(byte[] wrapper)
    {
        if (Volatile.Read(ref _disposed) == 0 && _initialized && _wrappers.IsEnabled) Send(new LeanProofWrapperMessage(wrapper));
    }

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gossip.RemovePeer(Broadcast);
        _stop.Cancel();
        _stop.Dispose();
        ClearProtocolEvents();
    }
}
