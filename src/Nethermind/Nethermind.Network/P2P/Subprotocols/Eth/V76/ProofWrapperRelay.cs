// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.TxPool;
using TxEventArgs = Nethermind.TxPool.TxEventArgs;
using EncodingNode = System.Collections.Generic.LinkedListNode<(Nethermind.Core.Crypto.ValueHash256 Hash, byte[] Encoding)>;
using WrapperNode = System.Collections.Generic.LinkedListNode<Nethermind.Consensus.ProofAggregation.ProofWrapper>;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 mempool aggregation: holds validated proof wrappers, fetches announced ones, serves them over
/// <c>eth/76</c>, and aggregates pending transactions no wrapper covers into new ones.</summary>
/// <remarks>
/// <para>A fetched wrapper is checked against its sender's announcement and any manifest before its proof is verified, and
/// its transactions are then submitted to the pool with their aggregated witnesses marked proven. Its proof is remembered by
/// <see cref="DependencyProofCache"/>, from which the block builder covers those transactions. It is relayed when it newly
/// admitted at least one transaction and no fewer than it rejected.</para>
/// <para>A block's aggregated proof is remembered too, so a transaction a reorg returns to the pool with aggregated witnesses
/// stays buildable and is re-aggregated into a wrapper by the next aggregation round.</para>
/// <para>Wrappers are exchanged only while EIP-8288 is active at the current time; received messages are ignored otherwise.
/// A wrapper's blob-carrying transactions, their blob data elided, go to EIP-8070 cell sampling.</para>
/// </remarks>
public sealed class ProofWrapperRelay : IDisposable
{
    /// <summary>How often pending transactions no held wrapper covers are aggregated into a new wrapper.</summary>
    public static readonly TimeSpan AggregationInterval = TimeSpan.FromSeconds(6);

    private const int MaxHeldWrappers = DependencyProofCache.MaxStatements / 2;
    private const long MaxHeldBytes = 64 * 1024 * 1024;

    // The held wrappers' proofs stay remembered for block building, so they must fit the statement cache together.
    private const long MaxHeldProofBytes = DependencyProofCache.MaxStatementBytes / 2;
    private const int MaxProducedTransactions = 256;
    private const int PeerRecordCapacity = 4096;
    private const int PeerTransactionCapacity = 16384;
    private const long MaxEncodingBytes = 32 * 1024 * 1024;

    /// <summary>Concurrent requests to one peer; its other hashes wait, or go to another announcer.</summary>
    private const int MaxRequestsPerPeer = 4;

    /// <summary>Invalid proofs and unanswered requests after which a peer is no longer fetched from. EIP-8518 forbids
    /// penalizing a peer for proof failures, but allows deprioritizing its fetches.</summary>
    private const int MaxFailuresPerPeer = 8;

    /// <summary>Requests in flight to all peers together.</summary>
    private const int MaxRequests = 64;

    /// <summary>Aggregate proofs not verified before that one node may have verified per window, and all nodes together:
    /// STARK verification is the relay's one expensive step, so even valid wrappers are rate limited.</summary>
    private const int MaxVerificationsPerNode = 30;
    private const int MaxVerifications = 300;
    private static readonly TimeSpan DefaultVerificationWindow = TimeSpan.FromMinutes(1);

    /// <summary>Node budgets remembered, beyond the connected peers: a node keeps its budget across reconnections.</summary>
    private const int NodeBudgetCapacity = 4096;

    /// <summary>Wanted wrappers, waiting or being fetched; announcements beyond them are not recorded.</summary>
    private const int MaxFetches = 4096;

    /// <summary>Blob-carrying transactions held, one per transaction, as the alternate to a recorded copy with other blob
    /// metadata.</summary>
    private const int MaxAlternates = 256;

    /// <summary>Wanted wrappers one peer may open, so that no peer fills <see cref="MaxFetches"/> alone.</summary>
    private const int MaxFetchesOpenedPerPeer = 512;

    /// <summary>Announcers recorded per wanted wrapper.</summary>
    private const int MaxAnnouncersPerFetch = 16;

    /// <summary>Wrappers above this length are first compared with held coverage through their manifests.</summary>
    private const int ManifestThreshold = 256 * 1024;

    private readonly ITxPool _txPool;
    private readonly ISparseBlobPoolPeerRegistry? _sparseBlobPoolPeerRegistry;
    private readonly DependencyProofCache _proofCache;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockTree _blockTree;
    private readonly ITimestamper _timestamper;
    private readonly bool _enabled;
    private readonly ILogger _logger;

    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, WrapperNode> _held = [];
    private readonly LinkedList<ProofWrapper> _heldOrder = [];
    private readonly Dictionary<(ValueHash256 Deps, ValueHash256 Txs), int> _heldDigests = [];
    private readonly Dictionary<ValueHash256, int> _coveredTxs = [];
    private long _heldBytes;
    private long _heldProofBytes;

    private readonly Dictionary<ValueHash256, Fetch> _fetches = [];
    private readonly LruKeyCache<ValueHash256> _discarded = new(PeerRecordCapacity, "discarded proof wrappers");
    private readonly EncodingCache _encodings = new(MaxEncodingBytes);
    private readonly LruCache<ValueHash256, ValueHash256> _sampledEncodings = new(PeerRecordCapacity, "blob transaction encodings awaiting cells");
    private readonly LruCache<ValueHash256, BlobAlternate> _blobAlternates = new(MaxAlternates, "blob transaction alternates");
    private readonly ConcurrentDictionary<IProofWrapperPeer, PeerState> _peers = new();
    private readonly LruCache<PublicKey, NodeBudget> _budgets = new(NodeBudgetCapacity, "proof wrapper node budgets");
    private readonly NodeBudget _global = new();
    private int _requests;
    private long _verificationWindow = (long)(DefaultVerificationWindow.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer? _timer;
    private int _aggregating;

    public ProofWrapperRelay(ITxPool txPool, DependencyProofCache proofCache, ISpecProvider specProvider, IBlockTree blockTree,
        ITimestamper timestamper, IBlocksConfig blocksConfig, ILogManager logManager,
        ISparseBlobPoolPeerRegistry? sparseBlobPoolPeerRegistry = null)
    {
        _txPool = txPool;
        _sparseBlobPoolPeerRegistry = sparseBlobPoolPeerRegistry;
        _proofCache = proofCache;
        _specProvider = specProvider;
        _blockTree = blockTree;
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<ProofWrapperRelay>();
        _enabled = blocksConfig.DependencyProofAggregation == DependencyProofAggregation.Mempool;
        if (!_enabled) return;

        _txPool.NewPending += OnNewPending;
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        _timer = new Timer(_ => Tick(), null, AggregationInterval, AggregationInterval);
    }

    /// <summary>Whether wrappers are exchanged now: this node aggregates its mempool and EIP-8288 is active at the current time.</summary>
    /// <remarks>EIP-8518 selects the mempool fork by the current time, independently of the head.</remarks>
    public bool IsActive => _enabled
        && _specProvider.GetSpec(new ForkActivation(_blockTree.Head?.Number ?? 0, _timestamper.UnixTime.Seconds)).IsEip8288Enabled;

    /// <summary>The window verification budgets are counted over; shortened by tests.</summary>
    internal TimeSpan VerificationWindow
    {
        set => _verificationWindow = (long)(value.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    internal int HeldCount
    {
        get
        {
            lock (_lock) return _held.Count;
        }
    }

    internal int FetchCount
    {
        get
        {
            lock (_lock) return _fetches.Count;
        }
    }

    internal bool TryGetHeld(in ValueHash256 hash, out ProofWrapper? wrapper)
    {
        lock (_lock)
        {
            bool found = _held.TryGetValue(hash, out WrapperNode? node);
            wrapper = node?.Value;
            return found;
        }
    }

    /// <summary>Registers an <c>eth/76</c> connection and offers it up to 64 held wrappers covering pending transactions,
    /// most recent first.</summary>
    internal void AddPeer(IProofWrapperPeer peer)
    {
        NodeBudget budget;
        lock (_lock)
        {
            if (!_budgets.TryGet(peer.NodeId, out budget!))
            {
                budget = new NodeBudget();
                _budgets.Set(peer.NodeId, budget);
            }
        }

        PeerState state = new(budget) { SparsePeer = peer as ISparseBlobPoolPeer };
        if (!_peers.TryAdd(peer, state) || !IsActive) return;

        List<ProofWrapper> offered = [];
        lock (_lock)
        {
            for (WrapperNode? node = _heldOrder.Last; node is not null && offered.Count < Eth76Protocol.MaxAnnouncedWrappers; node = node.Previous)
            {
                if (CoversPending(node.Value)) offered.Add(node.Value);
            }
        }

        AnnounceTo(peer, state, offered);
    }

    /// <summary>Unregisters a connection: every fetch it headed or waited on goes to its next announcer, and a fetch left
    /// without one is forgotten.</summary>
    internal void RemovePeer(IProofWrapperPeer peer)
    {
        if (!_peers.TryRemove(peer, out _)) return;

        lock (_lock) DropAnnouncer(peer);
        Schedule();
    }

    /// <summary>Records <paramref name="peer"/>'s announcements and fetches the wrappers that may add coverage.</summary>
    /// <returns>The fetches started, for callers that wait on them.</returns>
    internal Task OnAnnouncement(IProofWrapperPeer peer, IReadOnlyList<WrapperAnnouncement> announcements)
    {
        if (!IsActive) return Task.CompletedTask;

        lock (_lock)
        {
            // Looked up under the lock: a peer removed before it records nothing, and one removed after is cleaned up by
            // RemovePeer, which takes the lock once the peer is gone.
            if (!_peers.TryGetValue(peer, out PeerState? state)) return Task.CompletedTask;

            foreach (WrapperAnnouncement announcement in announcements)
            {
                state.Announced.Set(announcement.Hash, announcement);
                state.Exchanged.Set(announcement.Hash);
                if (_held.TryGetValue(announcement.Hash, out WrapperNode? held))
                {
                    // A peer announces only wrappers it holds, so it holds their transaction encodings too.
                    state.Exchange(held.Value);
                    continue;
                }

                // Equal digests identify coverage held already, despite different proof bytes.
                if (_discarded.Get(announcement.Hash) || _heldDigests.ContainsKey((announcement.DepsHash, announcement.TxsHash))) continue;

                if (state.Budget.Failures >= MaxFailuresPerPeer) continue;
                if (_fetches.TryGetValue(announcement.Hash, out Fetch? fetch))
                {
                    AddAnnouncer(fetch, peer);
                }
                else if (_fetches.Count < MaxFetches && state.Budget.Opened < MaxFetchesOpenedPerPeer)
                {
                    _fetches[announcement.Hash] = new Fetch(peer, state.Budget);
                    state.Budget.Opened++;
                }
            }
        }

        return Schedule();
    }

    /// <summary>Answers <c>GetProofWrappers</c> with the requested wrappers this node announced to <paramref name="peer"/>.</summary>
    internal IReadOnlyList<ServedWrapper> ServeWrappers(IProofWrapperPeer peer, IReadOnlyList<ValueHash256> hashes, bool compact)
    {
        List<ServedWrapper> served = [];
        if (!IsActive || !_peers.TryGetValue(peer, out PeerState? state)) return served;

        long size = 0;
        foreach (ValueHash256 hash in hashes)
        {
            if (size >= Eth76Protocol.SoftResponseLimit) break;
            if (!state.AnnouncedTo.Get(hash) || !TryGetHeld(hash, out ProofWrapper? wrapper)) continue;

            byte[] item = compact
                ? ProofWrapperCodec.Encode(wrapper!.EncodedTransactions, wrapper.Deps, wrapper.Proof,
                    transaction => state.Encodings.Get(ProofWrapperCodec.EncodingHash(transaction)))
                : wrapper!.Encoded;
            served.Add(new ServedWrapper(hash, item));
            size += item.Length;
            state.Exchange(wrapper);
        }

        return served;
    }

    /// <summary>Answers <c>GetProofWrapperManifests</c> with the manifests of requested wrappers this node announced to
    /// <paramref name="peer"/>.</summary>
    internal IReadOnlyList<ServedManifest> ServeManifests(IProofWrapperPeer peer, IReadOnlyList<ValueHash256> hashes)
    {
        List<ServedManifest> served = [];
        if (!IsActive || !_peers.TryGetValue(peer, out PeerState? state)) return served;

        long size = 0;
        foreach (ValueHash256 hash in hashes)
        {
            if (size >= Eth76Protocol.SoftResponseLimit) break;
            if (!state.AnnouncedTo.Get(hash) || !TryGetHeld(hash, out ProofWrapper? wrapper)) continue;

            ValueHash256[] manifest = new ValueHash256[wrapper!.Manifest.Length];
            for (int i = 0; i < manifest.Length; i++) manifest[i] = wrapper.Manifest[i].ValueHash256;
            served.Add(new ServedManifest(hash, manifest));
            size += manifest.Length * ValueHash256.MemorySize;
        }

        return served;
    }

    /// <summary>Holds a wrapper this node built and announces it.</summary>
    internal void AddLocal(ProofWrapper wrapper)
    {
        _proofCache.Remember(wrapper.Deps, wrapper.DepsHash, wrapper.Proof);
        if (Hold(wrapper)) AnnounceToAll(wrapper);
    }

    /// <summary>Assigns every waiting fetch to its first announcer with a free request, up to 32 hashes per request, and
    /// starts the requests.</summary>
    /// <remarks>A busy announcer is skipped for the next one; a fetch whose announcers are all busy waits for a request to
    /// finish, which schedules again.</remarks>
    /// <returns>The requests started.</returns>
    private Task Schedule()
    {
        Dictionary<IProofWrapperPeer, (NodeBudget Budget, List<ValueHash256> Hashes)> batches = [];
        lock (_lock)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach ((ValueHash256 hash, Fetch fetch) in _fetches)
            {
                if (fetch.Fetcher is not null || (fetch.DeferredAt != 0 && now - fetch.DeferredAt < _verificationWindow)) continue;
                foreach (IProofWrapperPeer announcer in fetch.Announcers)
                {
                    // A node whose verification budget is spent would have its wrappers fetched only to be dropped, unless
                    // their dependencies are proven here already.
                    if (!_peers.TryGetValue(announcer, out PeerState? state) || state.Budget.Failures >= MaxFailuresPerPeer) continue;
                    bool exempted = !CanVerify(state.Budget);
                    if (exempted && !IsProven(state, hash)) continue;
                    List<ValueHash256> batch;
                    if (batches.TryGetValue(announcer, out (NodeBudget Budget, List<ValueHash256> Hashes) started))
                    {
                        batch = started.Hashes;
                        if (batch.Count == Eth76Protocol.MaxRequestedWrappers) continue;
                    }
                    else
                    {
                        if (state.Budget.Requests >= MaxRequestsPerPeer || !WithinGlobalCap(state.Budget, _requests, MaxRequests)) continue;
                        state.Budget.Requests++;
                        _requests++;
                        batch = [];
                        batches[announcer] = (state.Budget, batch);
                    }

                    batch.Add(hash);
                    fetch.Fetcher = announcer;
                    fetch.Exempted = exempted;
                    break;
                }
            }
        }

        if (batches.Count == 0) return Task.CompletedTask;
        List<Task> requests = [];
        foreach ((IProofWrapperPeer peer, (NodeBudget budget, List<ValueHash256> batch)) in batches) requests.Add(FetchFrom(peer, budget, batch));
        return Task.WhenAll(requests);
    }

    private async Task FetchFrom(IProofWrapperPeer peer, NodeBudget budget, List<ValueHash256> hashes)
    {
        List<ValueHash256> assigned = [.. hashes];
        HashSet<ValueHash256> deferred = [];
        bool failed = false;
        try
        {
            if (!_peers.TryGetValue(peer, out PeerState? state)) return;

            Dictionary<ValueHash256, ValueHash256[]>? manifests = await FetchManifests(peer, state, hashes);
            if (hashes.Count == 0) return;

            List<ValueHash256> retryFull = [];
            await Request(peer, state, hashes, compact: true, manifests, retryFull, deferred);
            if (retryFull.Count != 0) await Request(peer, state, retryFull, compact: false, manifests, null, deferred);
        }
        catch (ProtocolViolation violation)
        {
            if (_logger.IsDebug) _logger.Debug($"Disconnecting an eth/76 peer: {violation.Message}");
            peer.Disconnect(violation.Message);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            failed = true;
        }
        catch (Exception exception)
        {
            if (_logger.IsDebug) _logger.Debug($"Proof wrapper fetch failed: {exception}");
        }
        finally
        {
            Finish(peer, budget, assigned, failed, deferred);
        }
    }

    /// <summary>Ends a request: each of its hashes still waiting on <paramref name="peer"/> goes to its next announcer, or
    /// is forgotten when none is left, so a later announcement fetches it again.</summary>
    /// <param name="failed">Whether the request went unanswered, which counts against the peer as an invalid proof does.</param>
    /// <param name="deferred">Hashes refused only because the global verification budget is spent: they keep their
    /// announcers and wait for the window to roll.</param>
    private void Finish(IProofWrapperPeer peer, NodeBudget budget, List<ValueHash256> hashes, bool failed, HashSet<ValueHash256> deferred)
    {
        lock (_lock)
        {
            budget.Requests--;
            _requests--;
            if (failed) CountFailure(peer, budget);

            foreach (ValueHash256 hash in hashes)
            {
                if (!_fetches.TryGetValue(hash, out Fetch? fetch) || fetch.Fetcher != peer) continue;
                fetch.Fetcher = null;
                // A deferral keeps every announcer: the fetch stays bounded by its opener's share and MaxFetches.
                if (deferred.Contains(hash))
                {
                    fetch.DeferredAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    continue;
                }

                fetch.Announcers.Remove(peer);
                if (fetch.Announcers.Count == 0) RemoveFetch(hash);
            }
        }

        Schedule();
    }

    /// <summary>Adds <paramref name="peer"/> as an announcer; when they are full, in place of one that failed before or is
    /// busy, so that peers announcing first cannot shut out the rest.</summary>
    /// <remarks>Called under <see cref="_lock"/>.</remarks>
    private void AddAnnouncer(Fetch fetch, IProofWrapperPeer peer)
    {
        List<IProofWrapperPeer> announcers = fetch.Announcers;
        if (announcers.Contains(peer)) return;
        if (announcers.Count < MaxAnnouncersPerFetch)
        {
            announcers.Add(peer);
            return;
        }

        for (int i = announcers.Count - 1; i >= 0; i--)
        {
            if (announcers[i] == fetch.Fetcher) continue;
            if (!_peers.TryGetValue(announcers[i], out PeerState? state) || state.Budget.Failures > 0 || state.Budget.Requests >= MaxRequestsPerPeer
                || !CanVerify(state.Budget))
            {
                announcers[i] = peer;
                return;
            }
        }
    }

    /// <remarks>Called under <see cref="_lock"/>.</remarks>
    private void RemoveFetch(in ValueHash256 hash)
    {
        if (_fetches.Remove(hash, out Fetch? fetch)) fetch.Opener.Opened--;
    }

    /// <remarks>Called under <see cref="_lock"/>.</remarks>
    private void CountFailure(IProofWrapperPeer peer, NodeBudget budget)
    {
        if (++budget.Failures == MaxFailuresPerPeer) DropAnnouncer(peer);
    }

    /// <summary>Whether a node may take one more of a globally capped resource. The cap is hard; its last quarter is kept for
    /// nodes that have served a valid proof, so that fresh nodes, however many, cannot shut out a proven one.</summary>
    private static bool WithinGlobalCap(NodeBudget budget, int used, int cap) =>
        used < cap - cap / 4 || (used < cap && budget.Successes > 0 && budget.Failures == 0);

    /// <summary>Whether a proof of the dependencies <paramref name="state"/>'s peer announced for <paramref name="hash"/> is
    /// remembered already, so its wrapper's proof is likely a cached one, which costs no verification.</summary>
    private bool IsProven(PeerState state, in ValueHash256 hash) =>
        state.Announced.TryGet(hash, out WrapperAnnouncement announcement) && _proofCache.TryGetProof(announcement.DepsHash, out _);

    /// <summary>Whether <paramref name="budget"/>'s window has a verification left.</summary>
    /// <remarks>Called under <see cref="_lock"/>. The global cap applies only where a verification happens, as a proof turns
    /// out cached only once fetched.</remarks>
    private bool CanVerify(NodeBudget budget)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        budget.Roll(now, _verificationWindow);
        return budget.Verifications < MaxVerificationsPerNode;
    }

    /// <summary>Takes one verification from <paramref name="budget"/>'s window, and from the global one.</summary>
    /// <remarks>Called under <see cref="_lock"/>.</remarks>
    private bool TryTakeVerification(NodeBudget budget)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        budget.Roll(now, _verificationWindow);
        _global.Roll(now, _verificationWindow);
        if (budget.Verifications >= MaxVerificationsPerNode
            || !WithinGlobalCap(budget, _global.Verifications, MaxVerifications)) return false;
        budget.Verifications++;
        _global.Verifications++;
        return true;
    }

    /// <summary>Removes <paramref name="peer"/> from every waiting fetch's announcers, forgetting fetches left without one;
    /// the fetches it is serving end when their request does.</summary>
    /// <remarks>Called under <see cref="_lock"/>.</remarks>
    private void DropAnnouncer(IProofWrapperPeer peer)
    {
        List<ValueHash256> orphaned = [];
        foreach ((ValueHash256 hash, Fetch fetch) in _fetches)
        {
            if (fetch.Fetcher == peer)
            {
                // A removed peer's request may never end, so its fetches are reassigned now; a late end finds them moved.
                if (_peers.ContainsKey(peer)) continue;
                fetch.Fetcher = null;
            }

            fetch.Announcers.Remove(peer);
            if (fetch.Announcers.Count == 0 && fetch.Fetcher is null) orphaned.Add(hash);
        }

        foreach (ValueHash256 hash in orphaned) RemoveFetch(hash);
    }

    /// <summary>Fetches the manifests of large wrappers while wrappers are held, and drops each whose transactions held
    /// wrappers all cover.</summary>
    private async Task<Dictionary<ValueHash256, ValueHash256[]>?> FetchManifests(IProofWrapperPeer peer, PeerState state, List<ValueHash256> hashes)
    {
        List<ValueHash256> large = [];
        if (HeldCount != 0)
        {
            foreach (ValueHash256 hash in hashes)
            {
                if (state.Announced.TryGet(hash, out WrapperAnnouncement announcement) && announcement.Length > ManifestThreshold) large.Add(hash);
            }
        }

        if (large.Count == 0) return null;

        IReadOnlyList<ServedManifest> served = await peer.GetProofWrapperManifests(large, _shutdown.Token);
        Dictionary<ValueHash256, ValueHash256[]> manifests = [];
        foreach (ServedManifest manifest in Match(served, large, static m => m.Hash))
        {
            // An announcement the bounded record forgot cannot be checked; the wrapper fetch then leaves it to another announcer.
            if (!state.Announced.TryGet(manifest.Hash, out WrapperAnnouncement announcement)) continue;
            ValueHash256[] txHashes = [.. manifest.TransactionHashes];
            if (!IsValidManifest(txHashes, announcement)) throw new ProtocolViolation("manifest does not match its announcement");
            if (AllCovered(txHashes))
            {
                hashes.Remove(manifest.Hash);
                Complete(manifest.Hash);
                continue;
            }

            manifests[manifest.Hash] = txHashes;
        }

        return manifests;
    }

    /// <remarks>A hash the peer does not supply stays assigned to it, and <see cref="Finish"/> hands it to the next announcer.</remarks>
    private async Task Request(IProofWrapperPeer peer, PeerState state, List<ValueHash256> hashes, bool compact,
        Dictionary<ValueHash256, ValueHash256[]>? manifests, List<ValueHash256>? retryFull, HashSet<ValueHash256> deferred)
    {
        for (int offset = 0; offset < hashes.Count; offset += Eth76Protocol.MaxRequestedWrappers)
        {
            List<ValueHash256> batch = hashes.GetRange(offset, Math.Min(Eth76Protocol.MaxRequestedWrappers, hashes.Count - offset));
            List<ValueHash256> pending = batch;
            // Hashes after the last returned wrapper were cut by the response limit and are asked again; earlier omissions
            // are unavailable from this peer.
            while (pending.Count != 0)
            {
                IReadOnlyList<ServedWrapper> served = await peer.GetProofWrappers(pending, compact, _shutdown.Token);
                if (served.Count == 0) break;

                int last = -1;
                foreach (ServedWrapper wrapper in Match(served, pending, static w => w.Hash))
                {
                    last = pending.IndexOf(wrapper.Hash);
                    Receive(peer, state, wrapper, compact, manifests, retryFull, deferred);
                }

                pending = pending.GetRange(last + 1, pending.Count - last - 1);
            }
        }
    }

    private void Receive(IProofWrapperPeer peer, PeerState state, ServedWrapper served, bool compact,
        Dictionary<ValueHash256, ValueHash256[]>? manifests, List<ValueHash256>? retryFull, HashSet<ValueHash256> deferred)
    {
        // An announcement the bounded record forgot cannot be checked, so the wrapper is left to another announcer.
        if (!state.Announced.TryGet(served.Hash, out WrapperAnnouncement announcement)) return;
        // A served item is never longer than its canonical wrapper, which the announcement gives; checked before decoding.
        if (served.Item.Length > announcement.Length) throw new ProtocolViolation("served wrapper exceeds its announced length");
        ProofWrapperCodec.Verdict verdict = ProofWrapperCodec.TryDecode(served.Item, compact ? ResolveEncoding : null, announcement.Length,
            out ProofWrapper? wrapper, out string? error, out ValueHash256 canonicalHash);
        switch (verdict)
        {
            case ProofWrapperCodec.Verdict.CacheMiss when retryFull is not null:
                retryFull.Add(served.Hash);
                return;
            case ProofWrapperCodec.Verdict.Malformed or ProofWrapperCodec.Verdict.CacheMiss:
                throw new ProtocolViolation(error!);
            // A wrapper is discarded only under the hash it really has, so a peer cannot have another's wrapper refused.
            case ProofWrapperCodec.Verdict.Invalid when canonicalHash != served.Hash:
                throw new ProtocolViolation("wrapper does not match its served hash");
            case ProofWrapperCodec.Verdict.Invalid:
                Discard(served.Hash, error!);
                return;
        }

        // The sender's hints and manifest are checked before the proof: a mismatch is its fault, whatever the proof.
        if (wrapper!.Hash.ValueHash256 != served.Hash || wrapper.Length != announcement.Length
            || wrapper.DepsHash != announcement.DepsHash || wrapper.TxsHash != announcement.TxsHash
            || (manifests?.TryGetValue(served.Hash, out ValueHash256[]? manifest) == true && !ManifestMatches(manifest, wrapper)))
        {
            throw new ProtocolViolation("wrapper does not match its announcement");
        }

        // A proof verified before costs nothing; any other takes from the sender's verification budget, and one beyond it is
        // left to another announcer, as no fault of the sender.
        bool verified = _proofCache.IsVerified(wrapper.DepsHash, Eip8288Constants.AggregatedVk, wrapper.Proof);
        if (!verified)
        {
            lock (_lock)
            {
                // A hash fetched only for its proven statement, from a peer out of budget, that comes with another proof is
                // the peer's fault; any other refusal is not, and waits or goes to the next announcer.
                if (_fetches.TryGetValue(served.Hash, out Fetch? fetch) && fetch.Fetcher == peer && fetch.Exempted)
                {
                    CountFailure(peer, state.Budget);
                    return;
                }

                if (!CanVerify(state.Budget)) return;
                if (!TryTakeVerification(state.Budget))
                {
                    deferred.Add(served.Hash);
                    return;
                }
            }
        }

        if (!verified && !ProofWrapperCodec.VerifyProof(wrapper, _proofCache))
        {
            lock (_lock) CountFailure(peer, state.Budget);
            Discard(served.Hash, "wrapper proof does not verify");
            return;
        }

        state.Exchange(wrapper);
        // Only a proof this node verified itself, which became new coverage, earns a place in the reserved part of the caps.
        if (Accept(wrapper, state) && !verified)
        {
            lock (_lock) state.Budget.Successes++;
        }
    }

    /// <summary>Admits a verified wrapper's transactions, and holds and relays it unless a retained witness fails.</summary>
    /// <returns>Whether the wrapper was held and admitted at least one transaction, which credits its sender; a wrapper whose
    /// transactions only await cells may still be relayed.</returns>
    private bool Accept(ProofWrapper wrapper, PeerState source)
    {
        // The proof stands for its dependencies whatever the transactions' other witnesses, so the transactions it admits
        // stay buildable even when the wrapper is discarded below.
        _proofCache.Remember(wrapper.Deps, wrapper.DepsHash, wrapper.Proof);
        ProofWrapperCodec.MarkCovered(wrapper);

        int admitted = 0;
        int rejected = 0;
        int known = 0;
        int pending = 0;
        int skipped = 0;
        for (int i = 0; i < wrapper.Transactions.Length; i++)
        {
            Transaction transaction = wrapper.Transactions[i];
            (Admission admission, AcceptTxResult result) = Admit(transaction, wrapper.EncodedTransactions[i], source);
            switch (admission)
            {
                case Admission.Admitted: admitted++; break;
                case Admission.Known: known++; break;
                // A blob-carrying transaction awaiting its cells is not admitted yet; EIP-8518 lets its wrapper circulate meanwhile.
                case Admission.Pending: pending++; break;
                case Admission.Skipped: skipped++; break;
                case Admission.Rejected when FrameTxClaimVerifier.IsWitnessError(result.Message):
                    Discard(wrapper.Hash.ValueHash256, $"retained witness of {transaction.Hash} failed: {result.Message}");
                    return false;
                default: rejected++; break;
            }
        }

        if (admitted == 0 && known == 0 && pending == 0)
        {
            // Skipped copies are no fault of the wrapper: it is left to the next announcer rather than discarded.
            if (skipped == 0) Discard(wrapper.Hash.ValueHash256, "no transaction of the wrapper is pending");
            return false;
        }

        // A transaction awaiting cells lets its wrapper circulate, as EIP-8518 allows, but does not offset a rejection.
        bool relay = admitted + pending > 0 && admitted >= rejected;
        bool held = Hold(wrapper);
        if (held && relay) AnnounceToAll(wrapper);
        Complete(wrapper.Hash.ValueHash256);
        return held && admitted > 0;
    }

    /// <summary>How a covered transaction fared.</summary>
    private enum Admission
    {
        Admitted,
        Known,

        /// <summary>A blob-carrying transaction this wrapper sent to cell sampling.</summary>
        Pending,

        /// <summary>A blob-carrying copy the sparse blobpool did not take, for its quotas, the sender's state, or another copy
        /// it is sampling: neither admitted nor rejected.</summary>
        Skipped,
        Rejected,
    }

    /// <summary>Submits a covered transaction: to the pool, or, carrying blobs whose data the wrapper elides, to EIP-8070 cell
    /// sampling, which submits it once its blobs are available.</summary>
    /// <remarks>The wrapper's sender is not recorded as a cell provider: only cell announcements are. A copy whose blob
    /// metadata differs from the one awaiting cells is kept as the alternate while that one is sampled, so no peer can restart
    /// sampling; it is recorded once the recorded copy failed validation or expired.</remarks>
    private (Admission Admission, AcceptTxResult Result) Admit(Transaction transaction, byte[] encoding, PeerState source)
    {
        if (!transaction.CarriesBlobs)
        {
            AcceptTxResult submitted = _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            return (submitted == AcceptTxResult.Accepted ? Admission.Admitted
                : submitted == AcceptTxResult.AlreadyKnown ? Admission.Known : Admission.Rejected, submitted);
        }

        // A blob copy this node cannot sample cannot be validated, so it counts against the wrapper.
        if (_sparseBlobPoolPeerRegistry is null || source.SparsePeer is not { } peer) return (Admission.Rejected, AcceptTxResult.Invalid);

        ValueHash256 txHash = transaction.Hash!.ValueHash256;
        ValueHash256 encodingHash = ProofWrapperCodec.EncodingHash(encoding);
        bool recordedAlready = _sparseBlobPoolPeerRegistry.HasRecordedTransaction(txHash);
        if (recordedAlready && _sampledEncodings.TryGet(txHash, out ValueHash256 recorded) && recorded != encodingHash)
        {
            // Kept as the one alternate, the first; a later one cannot displace it.
            if (!_blobAlternates.TryGet(txHash, out _)) _blobAlternates.Set(txHash, new BlobAlternate(transaction, encodingHash, peer));
            return (Admission.Skipped, AcceptTxResult.Accepted);
        }

        if (recordedAlready) return (Admission.Known, AcceptTxResult.AlreadyKnown);

        _sampledEncodings.Set(txHash, encodingHash);
        AcceptTxResult? result = _sparseBlobPoolPeerRegistry.RecordTransaction(peer, transaction);
        if (result is { } value) return (value ? Admission.Admitted : Admission.Rejected, value);
        // Pending only when this wrapper started its sampling, which passed the pool's checks.
        return _sparseBlobPoolPeerRegistry.HasRecordedTransaction(txHash) ? (Admission.Pending, AcceptTxResult.Accepted) : (Admission.Skipped, AcceptTxResult.Accepted);
    }

    private bool Hold(ProofWrapper wrapper)
    {
        lock (_lock)
        {
            if (_held.ContainsKey(wrapper.Hash.ValueHash256)) return false;

            _held[wrapper.Hash.ValueHash256] = _heldOrder.AddLast(wrapper);
            _heldBytes += wrapper.Length;
            _heldProofBytes += wrapper.Proof.Length;
            _heldDigests[(wrapper.DepsHash, wrapper.TxsHash)] = _heldDigests.GetValueOrDefault((wrapper.DepsHash, wrapper.TxsHash)) + 1;
            foreach (Hash256 txHash in wrapper.Manifest)
            {
                _coveredTxs[txHash.ValueHash256] = _coveredTxs.GetValueOrDefault(txHash.ValueHash256) + 1;
            }

            for (int i = 0; i < wrapper.EncodedTransactions.Length; i++)
            {
                _encodings.Set(ProofWrapperCodec.EncodingHash(wrapper.EncodedTransactions[i]), wrapper.EncodedTransactions[i]);
            }

            while (_heldOrder.Count > MaxHeldWrappers || _heldBytes > MaxHeldBytes || _heldProofBytes > MaxHeldProofBytes) Release(_heldOrder.First!.Value);
            return _held.ContainsKey(wrapper.Hash.ValueHash256);
        }
    }

    private void Release(ProofWrapper wrapper)
    {
        if (!_held.Remove(wrapper.Hash.ValueHash256, out WrapperNode? node)) return;

        _heldOrder.Remove(node);
        _heldBytes -= wrapper.Length;
        _heldProofBytes -= wrapper.Proof.Length;
        foreach (byte[] encoding in wrapper.EncodedTransactions) _encodings.Remove(ProofWrapperCodec.EncodingHash(encoding));
        Decrement(_heldDigests, (wrapper.DepsHash, wrapper.TxsHash));
        foreach (Hash256 txHash in wrapper.Manifest) Decrement(_coveredTxs, txHash.ValueHash256);
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        if (counts.TryGetValue(key, out int count) && count > 1) counts[key] = count - 1;
        else counts.Remove(key);
    }

    private void AnnounceToAll(ProofWrapper wrapper)
    {
        foreach ((IProofWrapperPeer peer, PeerState state) in _peers) AnnounceTo(peer, state, [wrapper]);
    }

    /// <summary>Announces the wrappers that add coverage for <paramref name="peer"/>: neither exchanged with it already nor
    /// covering only transactions wrappers exchanged with it cover.</summary>
    private static void AnnounceTo(IProofWrapperPeer peer, PeerState state, List<ProofWrapper> wrappers)
    {
        List<WrapperAnnouncement> announcements = [];
        foreach (ProofWrapper wrapper in wrappers)
        {
            if (state.Exchanged.Get(wrapper.Hash.ValueHash256) || state.Covers(wrapper)) continue;
            state.AnnouncedTo.Set(wrapper.Hash.ValueHash256);
            state.Exchanged.Set(wrapper.Hash.ValueHash256);
            state.Cover(wrapper);
            announcements.Add(new WrapperAnnouncement(wrapper.Length, wrapper.Hash.ValueHash256, wrapper.DepsHash, wrapper.TxsHash));
        }

        if (announcements.Count == 0) return;

        announcements.Sort(static (left, right) => left.Hash.CompareTo(right.Hash));
        for (int offset = 0; offset < announcements.Count; offset += Eth76Protocol.MaxAnnouncedWrappers)
        {
            peer.Announce(announcements.GetRange(offset, Math.Min(Eth76Protocol.MaxAnnouncedWrappers, announcements.Count - offset)));
        }
    }

    private void Complete(in ValueHash256 hash)
    {
        lock (_lock) RemoveFetch(hash);
    }

    private void Discard(in ValueHash256 hash, string reason)
    {
        if (_logger.IsDebug) _logger.Debug($"Discarded proof wrapper {hash}: {reason}");
        _discarded.Set(hash);
        Complete(hash);
    }

    private byte[]? ResolveEncoding(ValueHash256 encodingHash) => _encodings.TryGet(encodingHash);

    private bool AllCovered(ValueHash256[] txHashes)
    {
        lock (_lock)
        {
            foreach (ValueHash256 txHash in txHashes)
            {
                if (!_coveredTxs.ContainsKey(txHash)) return false;
            }
        }

        return true;
    }

    private bool CoversPending(ProofWrapper wrapper)
    {
        foreach (Hash256 txHash in wrapper.Manifest)
        {
            if (_txPool.TryGetPendingTransaction(txHash.ValueHash256, out _)) return true;
        }

        return false;
    }

    /// <summary>EIP-8518's manifest checks on arrival: nonempty, strictly ascending, smaller than the announced wrapper, and
    /// hashing to the announced <c>txs-hash</c>.</summary>
    private static bool IsValidManifest(ValueHash256[] txHashes, in WrapperAnnouncement announcement)
    {
        if (txHashes.Length == 0 || Serialization.Rlp.Rlp.LengthOfSequence(txHashes.Length * Serialization.Rlp.Rlp.LengthOfKeccakRlp) >= announcement.Length)
            return false;

        KeccakHash hash = KeccakHash.Create();
        for (int i = 0; i < txHashes.Length; i++)
        {
            if (i > 0 && txHashes[i - 1].CompareTo(txHashes[i]) >= 0) return false;
            hash.Update(txHashes[i].Bytes);
        }

        return hash.GenerateValueHash() == announcement.TxsHash;
    }

    private static bool ManifestMatches(ValueHash256[] manifest, ProofWrapper wrapper)
    {
        if (manifest.Length != wrapper.Manifest.Length) return false;
        for (int i = 0; i < manifest.Length; i++)
        {
            if (manifest[i] != wrapper.Manifest[i].ValueHash256) return false;
        }

        return true;
    }

    /// <summary>Checks that a response holds requested hashes in request order without repeats.</summary>
    private static IEnumerable<T> Match<T>(IReadOnlyList<T> served, List<ValueHash256> requested, Func<T, ValueHash256> hashOf)
    {
        int position = 0;
        foreach (T item in served)
        {
            ValueHash256 hash = hashOf(item);
            while (position < requested.Count && requested[position] != hash) position++;
            if (position == requested.Count) throw new ProtocolViolation("response is unrequested, repeated or out of order");
            position++;
            yield return item;
        }
    }

    private void OnNewPending(object? sender, TxEventArgs args)
    {
        Transaction tx = args.Transaction;
        if (tx.Type != TxType.FrameTx || FrameTxClaimVerifier.HasAggregatedWitness(tx)) return;

        // A pooled transaction normalizes to the encoding a compact response refers to.
        if (ProofWrapperCodec.Normalize(tx) is { } encoding) _encodings.Set(ProofWrapperCodec.EncodingHash(encoding), encoding);
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs args)
    {
        Block block = args.Block;
        if (block.DependencyProof is not [Eip8288Constants.DependencyProofVersionAggregated, ..] proof
            || !AggregatedDependencyProof.TryDecode(proof, out byte[] stark, out _))
        {
            return;
        }

        // The block was validated, so its proof stands for its leanSPHINCS dependencies: a reorg returning its transactions
        // to the pool can then still build and re-aggregate them.
        Task.Run(() => RememberBlockProof(block, stark));
    }

    internal void RememberBlockProof(Block block, byte[] stark)
    {
        List<AggregatedDependency> deps = AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(block));
        if (deps.Count != 0) _proofCache.Remember(deps, Eip8288Dependencies.ComputeDepsHash(deps), stark);
    }

    /// <summary>Runs every aggregation interval: fetches that waited on a spent verification window are scheduled again once
    /// it rolls, and pending transactions are aggregated.</summary>
    internal void Tick()
    {
        if (!_peers.IsEmpty) _ = Schedule();
        StartAggregation();
    }

    private void StartAggregation()
    {
        if (_peers.IsEmpty || !IsActive || Interlocked.Exchange(ref _aggregating, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                Prune();
                if (Aggregate() is { } wrapper) AddLocal(wrapper);
            }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Mempool aggregation failed: {exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _aggregating, 0);
            }
        });
    }

    /// <summary>Releases held wrappers none of whose transactions are pending any more, keeps the proofs of the rest
    /// remembered for block building, and records the alternate of each blob-carrying transaction whose recorded copy the
    /// sparse blobpool no longer holds, because its validation failed or it expired.</summary>
    internal void Prune()
    {
        RecordAlternates();

        List<ProofWrapper> held;
        lock (_lock) held = [.. _heldOrder];

        foreach (ProofWrapper wrapper in held)
        {
            if (CoversPending(wrapper))
            {
                _proofCache.Remember(wrapper.Deps, wrapper.DepsHash, wrapper.Proof);
                continue;
            }

            lock (_lock) Release(wrapper);
        }
    }

    /// <summary>Aggregates pending frame transactions no held wrapper covers into one wrapper.</summary>
    /// <remarks>A transaction with raw witnesses contributes them; one known only from a wrapper or a reorged block, which a
    /// held wrapper no longer covers, is re-aggregated from the proofs that cover it, and is left out when none does.</remarks>
    /// <returns>The wrapper, or <see langword="null"/> when there is nothing to aggregate.</returns>
    internal ProofWrapper? Aggregate()
    {
        Transaction[] pending = _txPool.GetPendingTransactions();
        List<(Transaction Tx, byte[] Encoding)> chosen = [];
        List<AggregatedDependency> witnessedDeps = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        List<AggregatedDependency> unwitnessed = [];
        int length = 0;
        foreach (Transaction tx in pending)
        {
            if (chosen.Count == MaxProducedTransactions) break;
            if (tx.Type != TxType.FrameTx || IsCovered(tx.Hash!) || ProofWrapperCodec.Normalize(tx) is not { } encoding) continue;

            bool aggregated = FrameTxClaimVerifier.HasAggregatedWitness(tx);
            List<AggregatedDependency> deps = ProofWrapperCodec.AggregatedDependencies(tx);
            if (aggregated && !_proofCache.Covers(deps)) continue;

            int added = Serialization.Rlp.Rlp.LengthOf(encoding);
            // The proof's bound is kept free, so the wrapper stays within MAX_WRAPPER_BYTES.
            if (length + added > ProofWrapperCodec.MaxWrapperBytes - Eip8288Constants.MaxAggregateProofBytes - 64 * 1024) continue;
            length += added;
            chosen.Add((tx, encoding));
            if (aggregated)
            {
                unwitnessed.AddRange(deps);
                continue;
            }

            FrameTxDependencyProof.Dependency[] resolved = FrameTxDependencyProof.Resolve(tx);
            for (int i = 0; i < resolved.Length; i++)
            {
                if (!AggregatedDependencyProof.IsAggregated(resolved[i])) continue;
                witnessedDeps.Add(Eip8288Dependencies.Map(resolved[i].Key, resolved[i].Msg));
                byte[] witness = new byte[Eip8288Constants.LeanSphincsWitnessBytes];
                resolved[i].Key.Bytes.CopyTo(witness);
                tx.ClaimWitnesses![i].CopyTo(witness.AsSpan(ValueHash256.MemorySize));
                witnesses.Add(witness);
            }
        }

        if (chosen.Count == 0) return null;

        List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize([.. witnessedDeps, .. unwitnessed]);
        List<RecursiveProofInput> covering = [];
        if (unwitnessed.Count != 0)
        {
            // The statements covering them may have been evicted since they were chosen; the next round retries.
            if (_proofCache.Covering(unwitnessed) is not { } statements) return null;
            foreach (DependencyProofCache.Statement statement in statements) covering.Add(new RecursiveProofInput(statement.Deps, statement.Proof));
        }

        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(canonical);
        byte[] proof = DependencyProofAggregator.Prove(DependencyProofAggregator.Combine(witnessedDeps, witnesses, canonical, [.. covering]),
            _proofCache, depsHash, _shutdown.Token);

        chosen.Sort(static (left, right) => left.Tx.Hash!.CompareTo(right.Tx.Hash!));
        byte[][] encodings = new byte[chosen.Count][];
        Transaction[] transactions = new Transaction[chosen.Count];
        for (int i = 0; i < chosen.Count; i++) (transactions[i], encodings[i]) = chosen[i];
        return ProofWrapperCodec.Create(encodings, transactions, canonical, proof);
    }

    private void RecordAlternates()
    {
        if (_sparseBlobPoolPeerRegistry is null) return;
        foreach (KeyValuePair<ValueHash256, BlobAlternate> entry in _blobAlternates.ToArray())
        {
            if (_sparseBlobPoolPeerRegistry.HasRecordedTransaction(entry.Key) || !_blobAlternates.Delete(entry.Key)) continue;
            BlobAlternate alternate = entry.Value;
            _sampledEncodings.Set(entry.Key, alternate.EncodingHash);
            _sparseBlobPoolPeerRegistry.RecordTransaction(alternate.Peer, alternate.Transaction);
        }
    }

    private bool IsCovered(Hash256 txHash)
    {
        lock (_lock) return _coveredTxs.ContainsKey(txHash.ValueHash256);
    }

    public void Dispose()
    {
        if (!_enabled) return;

        _timer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _txPool.NewPending -= OnNewPending;
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
    }

    /// <summary>Aggregate transaction encodings by encoding hash, least recently used first out, bounded by their bytes.</summary>
    internal sealed class EncodingCache(long maxBytes)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<ValueHash256, EncodingNode> _entries = [];
        private readonly LinkedList<(ValueHash256 Hash, byte[] Encoding)> _order = [];
        private long _bytes;

        public void Set(in ValueHash256 hash, byte[] encoding)
        {
            if (encoding.Length > maxBytes) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(hash, out EncodingNode? node))
                {
                    _order.Remove(node);
                    _order.AddLast(node);
                    return;
                }

                _entries[hash] = _order.AddLast((hash, encoding));
                _bytes += encoding.Length;
                while (_bytes > maxBytes) RemoveCore(_order.First!.Value.Hash);
            }
        }

        public byte[]? TryGet(in ValueHash256 hash)
        {
            lock (_lock) return _entries.TryGetValue(hash, out EncodingNode? node) ? node.Value.Encoding : null;
        }

        public void Remove(in ValueHash256 hash)
        {
            lock (_lock) RemoveCore(hash);
        }

        internal long Bytes
        {
            get
            {
                lock (_lock) return _bytes;
            }
        }

        private void RemoveCore(in ValueHash256 hash)
        {
            if (!_entries.Remove(hash, out EncodingNode? node)) return;
            _order.Remove(node);
            _bytes -= node.Value.Encoding.Length;
        }
    }

    /// <summary>The fetch and verification budget of one node, shared by its connections and kept across reconnections.</summary>
    /// <remarks>Guarded by the relay's lock.</remarks>
    private sealed class NodeBudget
    {
        public int Requests;
        public int Failures;
        public int Opened;
        public int Verifications;

        /// <summary>Valid proofs the node served in the current window that this node verified and kept as new coverage, which
        /// admit it to the reserved part of the global caps.</summary>
        public int Successes;
        private long _windowStart;

        /// <summary>Starts a new verification window once the current one has passed.</summary>
        public void Roll(long now, long window)
        {
            if (now - _windowStart < window) return;
            _windowStart = now;
            Verifications = 0;
            Successes = 0;
        }
    }

    /// <summary>A blob-carrying transaction from a verified wrapper, waiting for the recorded copy of it to give way.</summary>
    private sealed record BlobAlternate(Transaction Transaction, ValueHash256 EncodingHash, ISparseBlobPoolPeer Peer);

    /// <summary>A wanted wrapper: the peers that announced it, and the one it is being fetched from, if any.</summary>
    private sealed class Fetch(IProofWrapperPeer announcer, NodeBudget opener)
    {
        public List<IProofWrapperPeer> Announcers { get; } = [announcer];

        /// <summary>The peer whose announcement opened the fetch, which it counts against.</summary>
        public NodeBudget Opener { get; } = opener;
        public IProofWrapperPeer? Fetcher { get; set; }

        /// <summary>Whether it is being fetched from a peer out of verification budget, for its proven statement only.</summary>
        public bool Exempted { get; set; }

        /// <summary>When the spent global verification budget last deferred the fetch, in timestamp ticks; it is scheduled
        /// again a verification window later.</summary>
        public long DeferredAt { get; set; }
    }

    /// <summary>What one connection has exchanged: bounded records, so old entries may be forgotten.</summary>
    private sealed class PeerState(NodeBudget budget)
    {
        public NodeBudget Budget { get; } = budget;

        /// <summary>The connection as EIP-8070 cell sampling knows it, for blob-carrying transactions it delivers.</summary>
        public ISparseBlobPoolPeer? SparsePeer { get; init; }

        public LruCache<ValueHash256, WrapperAnnouncement> Announced { get; } = new(PeerRecordCapacity, "announced proof wrappers");
        public LruKeyCache<ValueHash256> AnnouncedTo { get; } = new(PeerRecordCapacity, "proof wrappers announced to a peer");
        public LruKeyCache<ValueHash256> Exchanged { get; } = new(PeerRecordCapacity, "proof wrappers exchanged with a peer");
        public LruKeyCache<ValueHash256> CoveredTxs { get; } = new(PeerTransactionCapacity, "transactions a peer's wrappers cover");
        public LruKeyCache<ValueHash256> Encodings { get; } = new(PeerTransactionCapacity, "transaction encodings a peer holds");

        /// <summary>Records a validated wrapper both sides know, whose transaction encodings the peer then holds.</summary>
        public void Exchange(ProofWrapper wrapper)
        {
            Exchanged.Set(wrapper.Hash.ValueHash256);
            Cover(wrapper);
            foreach (byte[] encoding in wrapper.EncodedTransactions) Encodings.Set(ProofWrapperCodec.EncodingHash(encoding));
        }

        public void Cover(ProofWrapper wrapper)
        {
            foreach (Hash256 txHash in wrapper.Manifest) CoveredTxs.Set(txHash.ValueHash256);
        }

        public bool Covers(ProofWrapper wrapper)
        {
            foreach (Hash256 txHash in wrapper.Manifest)
            {
                if (!CoveredTxs.Get(txHash.ValueHash256)) return false;
            }

            return true;
        }
    }

    /// <summary>A response or wrapper its sender is at fault for, which warrants disconnection.</summary>
    private sealed class ProtocolViolation(string message) : Exception(message);
}
