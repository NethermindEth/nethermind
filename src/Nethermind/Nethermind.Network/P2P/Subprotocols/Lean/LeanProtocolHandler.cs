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
    private readonly Lock _receiveLock = new();
    private bool _receiving;
    private LeanProofWrapperMessage? _active;
    private LeanProofWrapperMessage? _pending;
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
        if (!Session.HasAgreedCapability(new Capability(Code, Version)))
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean capability was not negotiated");
            return;
        }
        if (!_wrappers.IsEnabled || _blockTree.Genesis?.Hash is not { } genesis)
        {
            Dispose();
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
            if (_initialized || !Session.HasAgreedCapability(new Capability(Code, Version)))
            {
                Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Unexpected lean status");
                return true;
            }
            if (!_wrappers.IsEnabled || status.ChainId != _blockTree.ChainId || status.GenesisHash != _blockTree.Genesis?.Hash
                || !status.VerificationKey.AsSpan().SequenceEqual(Eip8288Constants.AggregatedVk))
            {
                ReceivedProtocolInitMsg(status);
                Dispose();
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
        LeanProofWrapperMessage wrapper = Deserialize<LeanProofWrapperMessage>(message.Content);
        lock (_receiveLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return true;
            if (_receiving)
            {
                _pending = wrapper;
                return true;
            }
            _receiving = true;
            _active = wrapper;
        }
        ScheduleReceive();
        return true;
    }

    private void ScheduleReceive()
    {
        try
        {
            if (!BackgroundTaskScheduler.TryScheduleBackgroundTask(true, Receive)) ClearReceive();
        }
        catch
        {
            ClearReceive();
            throw;
        }
    }

    private async ValueTask Receive(bool _, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, cancellationToken);
        bool completed = false;
        try
        {
            LeanProofWrapperMessage? message;
            lock (_receiveLock)
            {
                message = _active;
                _active = null;
                if (message is null || Volatile.Read(ref _disposed) != 0)
                {
                    _receiving = false;
                    completed = true;
                    return;
                }
            }
            linked.Token.ThrowIfCancellationRequested();
            ProofWrapperAcceptance result = await _wrappers.AcceptDetailedAsync(message.Wrapper, linked.Token);
            if (result.Status == ProofWrapperAcceptanceStatus.Invalid) throw new RlpException(result.Result.Error ?? "Invalid lean proof wrapper");
            message = null;
            bool schedule;
            lock (_receiveLock)
            {
                schedule = _pending is not null && Volatile.Read(ref _disposed) == 0;
                _active = schedule ? _pending : null;
                _pending = null;
                _receiving = schedule;
                completed = true;
            }
            if (schedule) ScheduleReceive();
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { }
        finally { if (!completed) ClearReceive(); }
    }

    private void ClearReceive()
    {
        lock (_receiveLock)
        {
            _receiving = false;
            _active = null;
            _pending = null;
        }
    }

    private bool Broadcast(byte[] wrapper)
        => Volatile.Read(ref _disposed) == 0 && !Session.IsClosing && _initialized && _wrappers.IsEnabled
            && SendWithResult(new LeanProofWrapperMessage(wrapper)) > 0;

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gossip.RemovePeer(Broadcast);
        _stop.Cancel();
        ClearReceive();
        _stop.Dispose();
        ClearProtocolEvents();
    }
}
