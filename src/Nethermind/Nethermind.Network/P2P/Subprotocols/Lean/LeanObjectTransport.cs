// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>
/// EIP-8437 proof object transport shared by every <c>lean/1</c> connection: announcements, request lifecycles, bounded
/// multi-peer reassembly, validation hand-off and serving of validated objects.
/// </summary>
/// <remarks>
/// All peer, request and assembly state is guarded by one lock; messages and penalties are emitted after it is released.
/// No part of an object is announced, served, admitted or reaggregated before it is reconstructed and fully validated.
/// </remarks>
public sealed partial class LeanObjectTransport : IBlockProofSidecarSource, IDisposable
{
    public static readonly ValueHash256 LocalProfile = LeanCommitment.ProfileId(Eip8288Constants.AggregatedVk);
    public const byte LocalKinds = 0b111;
    private const int MaxProducedProofs = 16;
    private const int MaxTransactionRecoveries = 16;
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);

    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly ITxPool _txPool;
    private readonly ProofWrapperService _wrappers;
    private readonly IHeaderDecoder _headerDecoder;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<LeanPeer> _peers = [];
    private readonly Dictionary<PublicKey, LeanNodeBudget> _nodes = [];
    private readonly LeanObjectStore _store = new();
    private readonly Dictionary<ValueHash256, LeanAssembly> _assemblies = [];
    private readonly LeanBoundedSet _tombstones = new(LeanLimits.MaxTombstones);
    private readonly Dictionary<ValueHash256, List<LeanPeer>> _hints = [];
    private readonly Queue<ValueHash256> _hintOrder = [];
    private readonly Dictionary<ValueHash256, RecursiveStark> _produced = [];
    private readonly Queue<ValueHash256> _producedOrder = [];
    private readonly Queue<LeanAssembly> _validation = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private TaskCompletionSource _hintSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _incompleteBytes;
    private long _completionBytes;
    private int _pendingValidations;
    private bool _validating;
    private bool _disposed;
    private long _lastStats;
    private readonly LeanStats _stats = new();

    public LeanObjectTransport(IBlockTree blockTree, ISpecProvider specProvider, ITxPool txPool, ProofWrapperService wrappers,
        ILogManager logManager, IHeaderDecoder? headerDecoder = null, TimeProvider? timeProvider = null)
    {
        _blockTree = blockTree;
        _specProvider = specProvider;
        _txPool = txPool;
        _wrappers = wrappers;
        _headerDecoder = headerDecoder ?? new HeaderDecoder();
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logManager.GetClassLogger<LeanObjectTransport>();
        _lastStats = _clock.GetTimestamp();
        _wrappers.WrapperValidated += OnWrapperValidated;
        _wrappers.InclusionListValidated += OnInclusionListValidated;
        _blockTree.NewHeadBlock += OnNewHead;
        _timer = _clock.CreateTimer(static state => ((LeanObjectTransport)state!).Tick(), this, LeanLimits.Tick, LeanLimits.Tick);
    }

    /// <summary>Whether the current head activates EIP-8288; the whole transport is inert otherwise.</summary>
    public bool IsEnabled => _wrappers.IsEnabled;

    internal int PeerCount { get { lock (_gate) return _peers.Count; } }
    internal int AssemblyCount { get { lock (_gate) return _assemblies.Count; } }
    internal int StoredObjects { get { lock (_gate) return _store.Count; } }
    internal long IncompleteBytes { get { lock (_gate) return _incompleteBytes; } }
    internal long CompletionBytes { get { lock (_gate) return _completionBytes; } }
    internal bool IsTombstoned(in ValueHash256 objectId) { lock (_gate) return _tombstones.Contains(objectId, _clock.GetTimestamp()); }
    internal bool IsStored(in ValueHash256 objectId) { lock (_gate) return _store.Contains(objectId); }

    /// <summary>The local Status, or null while the genesis is unknown.</summary>
    public LeanStatusMessage? CreateStatus() => _blockTree.Genesis?.Hash is { } genesis
        ? new LeanStatusMessage(_blockTree.ChainId, genesis.ValueHash256, [LocalProfile], LocalKinds, LeanLimits.MaxObjectBytes)
        : null;

    /// <summary>Registers a peer whose Status is compatible; null disables <c>lean/1</c> on that connection only.</summary>
    /// <param name="link">The connection.</param>
    /// <param name="status">The peer's Status.</param>
    /// <param name="nodeKey">The authenticated node key, whose connections share one set of budgets.</param>
    internal LeanPeer? Accept(ILeanLink link, LeanStatusMessage status, PublicKey? nodeKey = null)
    {
        if (!IsEnabled || status.ChainId != (UInt256)_blockTree.ChainId || _blockTree.Genesis?.Hash?.ValueHash256 != status.GenesisHash
            || Array.IndexOf(status.Profiles, LocalProfile) < 0)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 disabled for {link.Description}: incompatible Status");
            return null;
        }
        Outbox outbox = new();
        LeanPeer peer;
        lock (_gate)
        {
            if (_disposed) return null;
            LeanNodeBudget node = nodeKey is null ? new LeanNodeBudget(null)
                : _nodes.TryGetValue(nodeKey, out LeanNodeBudget? shared) ? shared : _nodes[nodeKey] = new LeanNodeBudget(nodeKey);
            peer = new(link, status, node);
            node.Connections.Add(peer);
            _peers.Add(peer);
            AnnounceStored(peer, outbox);
        }
        outbox.Flush();
        if (_logger.IsDebug) _logger.Debug($"lean/1 established with {link.Description}");
        return peer;
    }

    internal void Remove(LeanPeer peer)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            RemoveCore(peer);
            Schedule(outbox, _clock.GetTimestamp());
        }
        outbox.Flush();
    }

    private void RemoveCore(LeanPeer peer)
    {
        if (peer.Closed) return;
        peer.Closed = true;
        _peers.Remove(peer);
        peer.Node.Connections.Remove(peer);
        if (peer.Node is { Key: { } key, Connections.Count: 0 }) _nodes.Remove(key);
        foreach (LeanOutgoingRequest request in peer.Live.Values) Fail(request, peer);
        peer.Live.Clear();
        foreach (LeanServing serving in peer.Serving.Values)
        {
            serving.CompleteWritten = true;
            serving.Cancellation.Cancel();
        }
        peer.Serving.Clear();
        foreach (LeanAssembly assembly in _assemblies.Values) assembly.Sources.Remove(peer);
    }

    private void Fail(LeanOutgoingRequest request, LeanPeer peer)
    {
        switch (request)
        {
            case LeanObjectsRequest objects:
                objects.Completion.TrySetResult(null);
                break;
            case LeanTransactionsRequest transactions:
                transactions.Completion.TrySetResult(null);
                break;
            case LeanChunksRequest chunks:
                ReleaseInFlight(chunks, peer);
                break;
        }
    }

    private static void ReleaseInFlight(LeanChunksRequest request, LeanPeer peer)
    {
        foreach (int index in request.Indices)
            if (request.Assembly.InFlight[index] == peer) request.Assembly.InFlight[index] = null;
    }

    private void Violation(LeanPeer peer, string reason, Outbox outbox)
    {
        LeanMetrics.Record(0, LeanEvent.Violation);
        Interlocked.Increment(ref _stats.Violations);
        if (_logger.IsDebug) _logger.Debug($"lean/1 protocol violation by {peer}: {reason}");
        RemoveCore(peer);
        outbox.Penalize(peer, $"lean/1: {reason}");
        // Indices the peer held in flight go to the remaining sources.
        Schedule(outbox, _clock.GetTimestamp());
    }

    private bool CheckIncomingId(LeanPeer peer, ulong requestId, Outbox outbox)
    {
        // Each direction numbers requests consecutively from one, including refused requests.
        if (requestId != peer.LastIncoming + 1)
        {
            Violation(peer, $"request ID {requestId} after {peer.LastIncoming}", outbox);
            return false;
        }
        peer.LastIncoming = requestId;
        return true;
    }

    /// <summary>Resolves a response's request; null means the response is discarded or the peer was penalized.</summary>
    private T? FindLive<T>(LeanPeer peer, ulong requestId, Outbox outbox) where T : LeanOutgoingRequest
    {
        if (requestId == 0 || requestId > peer.LastIssued)
        {
            Violation(peer, $"response to unissued request {requestId}", outbox);
            return null;
        }
        // An issued request that is no longer live was cancelled or expired: discard before any allocation.
        if (!peer.Live.TryGetValue(requestId, out LeanOutgoingRequest? request)) return null;
        if (request is not T typed)
        {
            Violation(peer, $"response type does not match request {requestId}", outbox);
            return null;
        }
        return typed;
    }

    private ulong Issue(LeanPeer peer, LeanOutgoingRequest request)
    {
        peer.Live.Add(request.Id, request);
        return request.Id;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (LeanPeer peer in _peers.ToArray()) RemoveCore(peer);
            foreach (LeanAssembly assembly in _assemblies.Values) Drop(assembly);
            _assemblies.Clear();
        }
        _wrappers.WrapperValidated -= OnWrapperValidated;
        _wrappers.InclusionListValidated -= OnInclusionListValidated;
        _blockTree.NewHeadBlock -= OnNewHead;
        _timer.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Messages and penalties collected under the lock and emitted after it is released.</summary>
    private sealed class Outbox
    {
        private List<(LeanPeer Peer, LeanMessage Message)>? _messages;
        private List<(LeanPeer Peer, string Reason)>? _penalties;
        private List<CancellationTokenSource>? _cancellations;

        public void Send(LeanPeer peer, LeanMessage message) => (_messages ??= []).Add((peer, message));

        public void Penalize(LeanPeer peer, string reason) => (_penalties ??= []).Add((peer, reason));

        /// <summary>Cancels work after the lock is released, since cancellation callbacks may resume awaiting code inline.</summary>
        public void Cancel(CancellationTokenSource source) => (_cancellations ??= []).Add(source);

        public void Flush()
        {
            if (_cancellations is not null)
                foreach (CancellationTokenSource source in _cancellations)
                {
                    try { source.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            if (_messages is not null)
                foreach ((LeanPeer peer, LeanMessage message) in _messages)
                    if (!peer.Closed) peer.Link.Send(message);
            if (_penalties is not null)
                foreach ((LeanPeer peer, string reason) in _penalties) peer.Link.Penalize(reason);
        }
    }

    private sealed class LeanStats
    {
        public long AnnouncedIn;
        public long AnnouncedOut;
        public long Fetched;
        public long Reassembled;
        public long Validated;
        public long Invalid;
        public long LocalFailures;
        public long Expired;
        public long ChunksServed;
        public long ChunksReceived;
        public long ObjectsServed;
        public long Violations;
        public long SidecarsFetched;
    }
}
