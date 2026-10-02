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
public class LeanProtocolHandler : ZeroProtocolHandlerBase, IStaticProtocolInfo
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
    private readonly LeanReassemblyBudget _budget;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly Lock _receiveLock = new();
    private bool _receiving;
    private LeanProofWrapperMessage? _active;
    private LeanProofWrapperMessage? _pending;
    private bool _initialized;
    private int _disposed;
    private long _receiveWindow = Environment.TickCount64;
    private int _receivedBytes;

    public LeanProtocolHandler(ISession session, INodeStatsManager nodeStats, IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler, ILogManager logManager,
        IBlockTree blockTree, ProofWrapperService wrappers, LeanProofGossip gossip, LeanReassemblyBudget budget)
        : base(session, nodeStats, serializer, backgroundTaskScheduler, logManager)
    {
        _blockTree = blockTree;
        _wrappers = wrappers;
        _gossip = gossip;
        _budget = budget;
        _stopToken = _stop.Token;
    }

    public override void Init()
    {
        if (!Session.HasAgreedCapability(new Capability(Code, ProtocolVersion)))
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean capability was not negotiated");
            return;
        }
        if (_blockTree.Genesis?.Hash is not { } genesis)
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
            if (_initialized || !Session.HasAgreedCapability(new Capability(Code, ProtocolVersion)))
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
            lock (_receiveLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return true;
                _initialized = true;
                ReceivedProtocolInitMsg(status);
                _gossip.AddPeer(BroadcastAsync);
            }
            NotifyProtocolInitialized(new ProtocolInitializedEventArgs(this));
            return true;
        }
        if (message.PacketType != 1) return false;
        if (!_initialized)
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Lean wrapper before handshake");
            return true;
        }
        if (!_wrappers.IsEnabled) return true;
        lock (_receiveLock)
        {
            long now = Environment.TickCount64;
            if (now - _receiveWindow >= 1000)
            {
                _receiveWindow = now;
                _receivedBytes = 0;
            }
            // Bound repeated replacement allocations as well as retained queue memory.
            int length = message.Content.ReadableBytes;
            if (length > 2 * LeanProofStore.MaxWrapperBytes - _receivedBytes) return true;
            _receivedBytes += length;
        }
        LeanProofWrapperMessage? wrapper = DecodeWrapper(message);
        if (wrapper is null) return true;
        lock (_receiveLock)
        {
            if (Volatile.Read(ref _disposed) != 0) { wrapper.Dispose(); return true; }
            if (_receiving)
            {
                _pending?.Dispose();
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
                    message?.Dispose();
                    _receiving = false;
                    completed = true;
                    return;
                }
            }
            using (message)
            {
                linked.Token.ThrowIfCancellationRequested();
                ProofWrapperAcceptance result = await _wrappers.AcceptDetailedAsync(message.Wrapper, linked.Token);
                if (result.Status == ProofWrapperAcceptanceStatus.Invalid) throw new RlpException(result.Result.Error ?? "Invalid lean proof wrapper");
            }
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
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { if (!completed) ClearReceive(); }
    }

    private void ClearReceive()
    {
        lock (_receiveLock)
        {
            _receiving = false;
            _active?.Dispose();
            _active = null;
            _pending?.Dispose();
            _pending = null;
        }
    }

    protected virtual LeanProofWrapperMessage? DecodeWrapper(ZeroPacket message)
    {
        int length = message.Content.ReadableBytes;
        if (length is 0 or > LeanProofStore.MaxWrapperBytes) throw new RlpException("Invalid lean wrapper size");
        IDisposable? lease = _budget.TryRent(length, static () => { });
        if (lease is null) return null;
        try { return new(Deserialize<LeanProofWrapperMessage>(message.Content).Wrapper, lease); }
        catch { lease.Dispose(); throw; }
    }

    protected bool CanBroadcast => Volatile.Read(ref _disposed) == 0 && !Session.IsClosing && _initialized && _wrappers.IsEnabled;
    protected CancellationToken StopToken => _stopToken;

    protected virtual async ValueTask<bool> BroadcastAsync(byte[] wrapper, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, cancellationToken);
        return CanBroadcast && await Session.DeliverMessageAsync(new LeanProofWrapperMessage(wrapper), linked.Token).ConfigureAwait(false) > 0;
    }

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_receiveLock) _gossip.RemovePeer(BroadcastAsync);
        _stop.Cancel();
        ClearReceive();
        _stop.Dispose();
        ClearProtocolEvents();
    }
}
