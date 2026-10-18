// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>One EIP-8288 / EIP-8437 proof wrapper lifecycle event, as returned by <c>debug_proofWrapperEvents</c>.</summary>
/// <remarks>Only <see cref="Type"/>, <see cref="Seq"/> and <see cref="Time"/> are always set; the other fields depend on the type.</remarks>
public sealed class ProofWrapperEvent
{
    /// <summary>Position in the feed, starting at 1.</summary>
    public long Seq { get; internal set; }

    /// <summary>Unix time in milliseconds when the event was recorded.</summary>
    public double Time { get; internal set; }

    public required string Type { get; init; }
    public Hash256? Wrapper { get; init; }

    /// <summary>The connection the event concerns.</summary>
    public string? Peer { get; init; }

    /// <summary>The remote node's public key, when known.</summary>
    public string? PeerId { get; init; }

    /// <summary>The EIP-8437 message involved, such as <c>GetProofWrappers</c>.</summary>
    public string? Message { get; init; }

    public long? RequestId { get; init; }
    public string? Result { get; init; }
    public string? Detail { get; init; }
    public int? Mode { get; init; }
    public int? Length { get; init; }
    public Hash256[]? Transactions { get; init; }

    /// <summary>Dependencies as <c>scheme:data_hash:verification_key</c>.</summary>
    public string[]? Dependencies { get; init; }

    public int? Count { get; init; }
    public double? DurationMs { get; init; }
    public long? Block { get; init; }
    public Hash256? BlockHash { get; init; }
}

/// <summary>A page of <see cref="ProofWrapperEvent"/>s after a sequence number.</summary>
/// <param name="Events">Events after the requested sequence number, oldest first.</param>
/// <param name="Latest">The sequence number of the newest recorded event, 0 when none.</param>
/// <param name="Missed">Events after the requested sequence number that the ring buffer already overwrote.</param>
/// <param name="Capacity">How many events the ring buffer keeps.</param>
/// <param name="Time">The node's Unix time in milliseconds, to align clocks between nodes.</param>
public sealed record ProofWrapperEventPage(ProofWrapperEvent[] Events, long Latest, long Missed, int Capacity, double Time);

/// <summary>Mode, transactions and dependencies of a wrapper, for <see cref="ProofWrapperEvent"/>s.</summary>
public sealed record ProofWrapperDetails(int Mode, Hash256[] Transactions, string[] Dependencies, int Length);

/// <summary>
/// Debug-only bounded record of what happens to proof wrappers on this node: EIP-8437 announcements, retrieval, envelope
/// recovery, validation and relay, and EIP-8288 admission, aggregation, the upgrade rule and inclusion.
/// </summary>
/// <remarks>
/// Disabled when the capacity is 0, the default: callers check <see cref="IsEnabled"/> before building an event, so a disabled
/// feed costs one field read per call site and never parses wrappers or subscribes to chain events. When enabled, the newest
/// <see cref="Capacity"/> events are kept in a ring buffer.
/// </remarks>
public sealed class ProofWrapperEventFeed : IDisposable
{
    private const int MaxDescribedWrappers = 256;
    private const int MaxPageSize = 2_000;

    /// <summary>A feed that records nothing.</summary>
    public static ProofWrapperEventFeed Disabled { get; } = new(0);

    private readonly ProofWrapperEvent?[] _ring;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, ProofWrapperDetails> _details = [];
    private readonly Queue<ValueHash256> _detailsOrder = [];
    private readonly IBlockTree? _blockTree;
    private readonly ITxPool? _txPool;
    private long _latest;

    public ProofWrapperEventFeed(int capacity, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _ring = new ProofWrapperEvent?[capacity];
        _clock = clock ?? TimeProvider.System;
        IsEnabled = capacity > 0;
    }

    /// <summary>Also records the inclusion and pool eviction of dependency-bearing transactions while enabled.</summary>
    public ProofWrapperEventFeed(int capacity, IBlockTree blockTree, ITxPool txPool, TimeProvider? clock = null) : this(capacity, clock)
    {
        if (!IsEnabled) return;
        _blockTree = blockTree;
        _txPool = txPool;
        blockTree.BlockAddedToMain += OnBlockAddedToMain;
        txPool.EvictedPending += OnEvictedPending;
    }

    public bool IsEnabled { get; }
    public int Capacity => _ring.Length;

    public void Record(ProofWrapperEvent wrapperEvent)
    {
        if (!IsEnabled) return;
        double time = UnixMilliseconds();
        lock (_lock)
        {
            wrapperEvent.Seq = ++_latest;
            wrapperEvent.Time = time;
            _ring[(_latest - 1) % _ring.Length] = wrapperEvent;
        }
    }

    /// <summary>Returns up to <paramref name="limit"/> events recorded after sequence number <paramref name="after"/>, oldest first.</summary>
    public ProofWrapperEventPage Read(long after, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        double now = UnixMilliseconds();
        lock (_lock)
        {
            long oldest = Math.Max(1, _latest - _ring.Length + 1);
            long first = Math.Max(after + 1, oldest);
            long missed = Math.Max(0, Math.Min(oldest, _latest + 1) - Math.Max(after, 0) - 1);
            long last = Math.Min(_latest, first + limit - 1);
            ProofWrapperEvent[] events = new ProofWrapperEvent[Math.Max(0, last - first + 1)];
            for (long seq = first; seq <= last; seq++) events[seq - first] = _ring[(seq - 1) % _ring.Length]!;
            return new(events, _latest, missed, _ring.Length, now);
        }
    }

    private double UnixMilliseconds() => (_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalMilliseconds;

    /// <summary>The mode, transactions and dependencies of an encoded wrapper, or null when it does not decode.</summary>
    /// <remarks>Decoded once per wrapper while it stays among the most recently described ones.</remarks>
    public ProofWrapperDetails? Describe(byte[] wrapper)
    {
        if (!IsEnabled) return null;
        ValueHash256 hash = ValueKeccak.Compute(wrapper);
        lock (_lock)
            if (_details.TryGetValue(hash, out ProofWrapperDetails? known)) return known;
        MempoolWrapper decoded;
        try
        {
            RlpReader reader = new(wrapper);
            decoded = MempoolWrapperDecoder.Instance.Decode(ref reader);
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException)
        {
            return null;
        }
        return Describe(decoded, hash, wrapper.Length);
    }

    /// <inheritdoc cref="Describe(byte[])"/>
    public ProofWrapperDetails? Describe(MempoolWrapper wrapper, in ValueHash256 hash, int length)
    {
        if (!IsEnabled) return null;
        Hash256[] transactions = new Hash256[wrapper.Transactions.Count];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = wrapper.Transactions[i].Hash ?? wrapper.Transactions[i].Full!.Hash!;
        string[] dependencies = new string[wrapper.Deps.Count];
        for (int i = 0; i < dependencies.Length; i++) dependencies[i] = DescribeDependency(wrapper.Deps[i]);
        ProofWrapperDetails details = new(wrapper.Mode, transactions, dependencies, length);
        lock (_lock)
        {
            if (_details.TryAdd(hash, details))
            {
                _detailsOrder.Enqueue(hash);
                if (_detailsOrder.Count > MaxDescribedWrappers) _details.Remove(_detailsOrder.Dequeue());
            }
        }
        return details;
    }

    public static string DescribeDependency(in FrameDependency dependency)
    {
        string scheme = dependency.Scheme switch
        {
            Eip8288Constants.LeanSphincsScheme => "leanSPHINCS",
            Eip8288Constants.LeanStarkScheme => "leanSTARK",
            _ => $"0x{dependency.Scheme:x2}"
        };
        return $"{scheme}:{dependency.DataHash}:{dependency.VerificationKey}";
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        foreach (Transaction transaction in e.Block.Transactions)
        {
            if (!Eip8288Dependencies.IsDependencyBearing(transaction)) continue;
            Record(new()
            {
                Type = ProofWrapperEventTypes.Included,
                Transactions = [transaction.Hash ?? transaction.CalculateHash()],
                Block = (long)e.Block.Number,
                BlockHash = e.Block.Hash
            });
        }
    }

    private void OnEvictedPending(object? sender, TxEventArgs e)
    {
        if (!Eip8288Dependencies.IsDependencyBearing(e.Transaction)) return;
        Record(new() { Type = ProofWrapperEventTypes.PoolEvicted, Transactions = [e.Transaction.Hash!], Detail = "evicted by pool policy" });
    }

    public void Dispose()
    {
        if (_blockTree is not null) _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        if (_txPool is not null) _txPool.EvictedPending -= OnEvictedPending;
    }
}

/// <summary>The <see cref="ProofWrapperEvent.Type"/> values, named after the EIP-8437 and EIP-8288 steps they record.</summary>
public static class ProofWrapperEventTypes
{
    public const string PeerConnected = "peer-connected";
    public const string PeerDisconnected = "peer-disconnected";
    public const string AnnouncementReceived = "announcement-received";
    public const string AnnouncementsIgnored = "announcements-ignored";
    public const string WrapperRequested = "wrapper-requested";
    public const string WrapperReceived = "wrapper-received";
    public const string WrapperUnavailable = "wrapper-unavailable";
    public const string WrapperTruncated = "wrapper-truncated";
    public const string RequestExpired = "request-expired";
    public const string FetchAbandoned = "fetch-abandoned";
    public const string ValidationStarted = "validation-started";
    public const string EnvelopeRequested = "envelope-requested";
    public const string EnvelopeResponse = "envelope-response";
    public const string EnvelopeRecovery = "envelope-recovery";
    public const string ClaimsChecked = "claims-checked";
    public const string Verdict = "verdict";
    public const string Violation = "violation";
    public const string Offered = "offered";
    public const string Announced = "announced";
    public const string Served = "served";
    public const string EnvelopeServed = "envelope-served";
    public const string Submitted = "submitted";
    public const string VerdictCached = "verdict-cached";
    public const string ProofVerified = "proof-verified";
    public const string Admitted = "admitted";
    public const string Aggregated = "aggregated";
    public const string VerifierChanged = "verifier-changed";
    public const string UpgradeKept = "upgrade-kept";
    public const string UpgradeDropped = "upgrade-dropped";
    public const string UpgradeEvicted = "upgrade-evicted";
    public const string PoolEvicted = "pool-evicted";
    public const string Included = "included";
    public const string ProofStep = "proof-step";
    public const string RecursiveVerify = "recursive-verify";
    public const string ProductionScheduled = "production-scheduled";
    public const string ProductionProof = "production-proof";
    public const string ProductionPass = "production-pass";
    public const string Gauges = "gauges";
}
