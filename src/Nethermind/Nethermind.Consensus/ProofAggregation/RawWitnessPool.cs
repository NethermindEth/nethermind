// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>EIP-8437 mode-2 raw candidates: wrappers whose raw signatures passed the native checks of recognized programs, held
/// for relay and for provers apart from the transaction pool and the proof store.</summary>
/// <remarks>
/// Raw acceptance establishes no proof coverage, so held transactions are never pending: they create no inclusion-list
/// obligation, cannot reach a block and take none of the proof store's capacity. Their bytes and native verifications have
/// budgets of their own, globally and per source, so raw traffic cannot exhaust what proof-backed traffic uses. Candidates
/// leave when a covering proof makes their transactions pending, when a transaction expires, becomes stale or fails its
/// validation prefix, or after <see cref="MaxAge"/>.
/// </remarks>
public sealed class RawWitnessPool : IDisposable
{
    /// <summary>Encoded raw wrapper bytes held in total.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;
    /// <summary>Encoded raw wrapper bytes one source may hold.</summary>
    public const long MaxBytesPerSource = 1024 * 1024;
    /// <summary>Raw wrappers held in total.</summary>
    public const int MaxWrappers = 512;
    /// <summary>The share of <see cref="MaxWrappers"/> and <see cref="MaxBytes"/> local submissions may hold; peers keep the rest.</summary>
    public const int MaxLocalWrappers = MaxWrappers / 4;
    public const long MaxLocalBytes = MaxBytes / 4;
    /// <summary>Verifications per second, in total and per source: native raw-signature checks, ordinary witnesses and
    /// validation-prefix simulations; each refills to one second's worth.</summary>
    public const double MaxVerificationsPerSecond = 64;
    public const double MaxVerificationsPerSourcePerSecond = 16;
    /// <summary>Raw wrappers validated at once; more are declined until one finishes.</summary>
    public const int MaxConcurrentValidations = 4;
    /// <summary>Sources whose allowances are remembered; the least recently charged is forgotten first.</summary>
    public const int MaxTrackedSources = 4096;
    /// <summary>Invalid raw wrapper hashes remembered, so a repeat is declined without validation.</summary>
    public const int MaxRememberedInvalid = 1024;
    /// <summary>How long a candidate waits for a prover before it is dropped.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public enum Admission { Added, Known, Full }

    /// <summary>A held raw wrapper.</summary>
    /// <param name="source">What admitted it: the peer connection, or null for a local submission.</param>
    /// <param name="identity">The stable identity its budgets are charged to, such as the peer's node ID.</param>
    /// <param name="local">Whether it was submitted locally, which its proven coverage keeps.</param>
    public sealed class Entry(byte[] wrapper, ValueHash256 hash, MempoolWrapper decoded, Eip8288Verifier verifier, object? source, object identity,
        bool local, long added)
    {
        public byte[] Wrapper { get; } = wrapper;
        public ValueHash256 Hash { get; } = hash;
        public MempoolWrapper Decoded { get; } = decoded;

        /// <summary>The verifier the wrapper was validated under.</summary>
        public Eip8288Verifier Verifier { get; internal set; } = verifier;
        public object? Source { get; } = source;
        internal object Identity { get; } = identity;
        public bool Local { get; } = local;
        internal long Added { get; } = added;

        /// <summary>Whether a prover has tried this candidate, so it is not retried every cycle.</summary>
        public bool Attempted { get; set; }
    }

    /// <summary>A refilling allowance; a charge above one second's worth is taken from a full allowance, leaving it in debt, so
    /// every admissible wrapper can be charged however large.</summary>
    private sealed class Allowance(double perSecond, TimeProvider clock)
    {
        private readonly double _rate = perSecond;
        private double _available = perSecond;
        private long _updated = clock.GetTimestamp();

        public bool TryTake(double amount)
        {
            long now = clock.GetTimestamp();
            _available = Math.Min(_rate, _available + clock.GetElapsedTime(_updated, now).TotalSeconds * _rate);
            _updated = now;
            if (Math.Min(amount, _rate) > _available) return false;
            _available -= amount;
            return true;
        }

        public void Refund(double amount) => _available = Math.Min(_rate, _available + amount);
    }

    private static readonly object LocalSource = new();
    /// <summary>The owner of a peer's wrapper admitted without a source or identity, kept apart from local submissions.</summary>
    private static readonly object UnknownPeer = new();

    private readonly ILogger _logger;
    private readonly ITxPool _txPool;
    private int _revalidating;
    private readonly IChainHeadInfoProvider _head;
    private readonly IFrameTxPrefixSimulator? _simulator;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<ValueHash256, Entry> _entries = [];
    private readonly LinkedList<ValueHash256> _order = new();
    private readonly Dictionary<ValueHash256, List<ValueHash256>> _byTransaction = [];
    private readonly Dictionary<object, long> _sourceBytes = [];
    private readonly Allowance _verifications;
    private readonly Dictionary<object, LinkedListNode<(object Identity, Allowance Allowance)>> _sourceVerifications = [];
    private readonly LinkedList<(object Identity, Allowance Allowance)> _sourceOrder = new();
    private readonly HashSet<(ValueHash256 Wrapper, ValueHash256 Verifier)> _invalid = [];
    private readonly Queue<(ValueHash256 Wrapper, ValueHash256 Verifier)> _invalidOrder = new();
    private int _validations;
    private int _localCount;

    public RawWitnessPool(ITxPool txPool, IChainHeadInfoProvider head, ITxPoolConfig config, IFrameTxPrefixSimulator? simulator = null,
        TimeProvider? clock = null, ILogManager? logManager = null)
    {
        _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<RawWitnessPool>();
        _txPool = txPool;
        _head = head;
        _simulator = simulator;
        _clock = clock ?? TimeProvider.System;
        _verifications = new(MaxVerificationsPerSecond, _clock);
        IsEnabled = config.RecognizedPrograms.Length != 0;
        if (!IsEnabled) return;
        _txPool.NewPending += OnNewPending;
        _txPool.TxPoolHeadChanged += OnHeadChanged;
    }

    /// <summary>Whether local policy recognizes any program, without which mode 2 is ignored.</summary>
    public bool IsEnabled { get; }

    public int Count { get { lock (_gate) return _entries.Count; } }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    private long _bytes;

    /// <summary>Takes <paramref name="count"/> verifications from the global budget and that of <paramref name="identity"/>, a
    /// stable source identity (a peer's node ID) or null for local submissions, so a reconnecting peer keeps its spent budget.</summary>
    /// <returns>False when either is spent, which permits refusal without penalty.</returns>
    public bool TryTakeVerifications(object? identity, int count)
    {
        lock (_gate)
        {
            Allowance perSource = SourceAllowance(identity ?? LocalSource);
            if (!_verifications.TryTake(count)) return false;
            if (perSource.TryTake(count)) return true;
            _verifications.Refund(count);
            return false;
        }
    }

    private Allowance SourceAllowance(object identity)
    {
        if (_sourceVerifications.Remove(identity, out LinkedListNode<(object Identity, Allowance Allowance)>? node)) _sourceOrder.Remove(node);
        else
        {
            if (_sourceVerifications.Count == MaxTrackedSources)
            {
                _sourceVerifications.Remove(_sourceOrder.First!.Value.Identity);
                _sourceOrder.RemoveFirst();
            }
            node = new((identity, new Allowance(MaxVerificationsPerSourcePerSecond, _clock)));
        }
        _sourceOrder.AddLast(node);
        _sourceVerifications[identity] = node;
        return node.Value.Allowance;
    }

    /// <summary>Takes one of the <see cref="MaxConcurrentValidations"/> validation slots, released by <see cref="ExitValidation"/>.</summary>
    public bool TryEnterValidation()
    {
        if (Interlocked.Increment(ref _validations) <= MaxConcurrentValidations) return true;
        Interlocked.Decrement(ref _validations);
        return false;
    }

    public void ExitValidation() => Interlocked.Decrement(ref _validations);

    /// <summary>The held wrapper <paramref name="wrapper"/>, so a repeat needs no decoding or validation.</summary>
    public bool TryGet(in ValueHash256 wrapper, out Entry? entry)
    {
        lock (_gate) return _entries.TryGetValue(wrapper, out entry);
    }

    /// <summary>Whether <paramref name="wrapper"/> is held, so a repeat needs no validation.</summary>
    public bool Contains(in ValueHash256 wrapper)
    {
        lock (_gate) return _entries.ContainsKey(wrapper);
    }

    /// <summary>Whether <paramref name="wrapper"/> was found invalid under <paramref name="verifier"/>.</summary>
    public bool IsKnownInvalid(in ValueHash256 wrapper, in Eip8288Verifier verifier)
    {
        lock (_gate) return _invalid.Contains((wrapper, verifier.AggregatedVk));
    }

    public void RememberInvalid(in ValueHash256 wrapper, in Eip8288Verifier verifier)
    {
        lock (_gate)
        {
            if (!_invalid.Add((wrapper, verifier.AggregatedVk))) return;
            _invalidOrder.Enqueue((wrapper, verifier.AggregatedVk));
            if (_invalidOrder.Count > MaxRememberedInvalid) _invalid.Remove(_invalidOrder.Dequeue());
        }
    }

    /// <summary>The ordinary checks a raw candidate's transaction must pass, as its pool admission would: not expired, its
    /// nonce not stale and, with <paramref name="simulate"/>, a validation prefix that authorizes it at the head.</summary>
    /// <returns>Null when it passes; otherwise why not, with <paramref name="indeterminate"/> set when the simulation says
    /// nothing about the transaction.</returns>
    public string? CheckTransaction(Transaction transaction, bool simulate, bool local, out bool indeterminate)
    {
        indeterminate = false;
        if (FrameTxValidation.TryGetExpiryDeadline(transaction, out ulong deadline) && _head.HeadTimestamp > deadline)
            return "frame transaction expired";
        if (transaction.SenderAddress is not { } sender) return "frame transaction has no sender";
        if (transaction.NonceKeys is not { Length: > 0 } && transaction.Nonce < _head.ReadOnlyStateProvider.GetNonce(sender))
            return "frame transaction nonce is stale";
        if (!simulate || _simulator is null) return null;
        FrameTxSimulationResult result = _simulator.Simulate(transaction, local: local);
        if (result.Outcome == FrameTxSimulationOutcome.Accepted) return null;
        indeterminate = result.Outcome == FrameTxSimulationOutcome.Undecided || result.Indeterminate;
        return result.Reason ?? "validation prefix rejected";
    }

    /// <summary>Holds a validated raw wrapper within the byte budgets.</summary>
    /// <remarks>Local submissions and peers have separate partitions, so neither can evict the other. Local submissions hold at most
    /// <see cref="MaxLocalWrappers"/> and <see cref="MaxLocalBytes"/>, beyond which their own oldest give way. Peers share the rest:
    /// beyond its own budget a peer is refused, and when the partition is full the peer holding the most wrappers, then bytes, if
    /// another, gives way, oldest first, so one peer cannot keep the others out.</remarks>
    public Admission TryAdd(byte[] wrapper, in ValueHash256 hash, MempoolWrapper decoded, Eip8288Verifier verifier, object? source,
        object? identity = null, bool local = false)
    {
        object owner = local ? LocalSource : identity ?? source ?? UnknownPeer;
        lock (_gate)
        {
            if (_entries.ContainsKey(hash)) return Admission.Known;
            PruneExpired();
            _sourceBytes.TryGetValue(owner, out long sourceBytes);
            if (local ? !MakeLocalRoom(wrapper.Length) : sourceBytes + wrapper.Length > MaxBytesPerSource || !MakeRoom(owner, wrapper.Length))
                return Admission.Full;
            Entry entry = new(wrapper, hash, decoded, verifier, source, owner, local, _clock.GetTimestamp());
            _entries.Add(hash, entry);
            if (local) _localCount++;
            _order.AddLast(hash);
            _bytes += wrapper.Length;
            _sourceBytes[owner] = _sourceBytes.GetValueOrDefault(owner) + wrapper.Length;
            foreach (Transaction transaction in decoded.Transactions)
            {
                ValueHash256 transactionHash = transaction.Hash!.ValueHash256;
                if (!_byTransaction.TryGetValue(transactionHash, out List<ValueHash256>? holders)) _byTransaction[transactionHash] = holders = [];
                holders.Add(hash);
            }
            return Admission.Added;
        }
    }

    private long LocalBytes => _sourceBytes.GetValueOrDefault(LocalSource);

    /// <summary>Evicts the oldest local wrappers until <paramref name="bytes"/> more fit the local partition.</summary>
    private bool MakeLocalRoom(int bytes)
    {
        if (bytes > MaxLocalBytes) return false;
        while (_localCount >= MaxLocalWrappers || LocalBytes + bytes > MaxLocalBytes)
        {
            foreach (ValueHash256 hash in _order)
            {
                if (!_entries[hash].Local) continue;
                RemoveLocked(hash);
                break;
            }
        }
        return true;
    }

    /// <summary>Evicts the largest other peer's oldest wrappers, largest by count, then bytes, until <paramref name="bytes"/> more fit
    /// the peers' partition.</summary>
    private bool MakeRoom(object owner, int bytes)
    {
        while (_entries.Count - _localCount >= MaxWrappers - MaxLocalWrappers || _bytes - LocalBytes + bytes > MaxBytes - MaxLocalBytes)
        {
            Dictionary<object, int> counts = [];
            foreach (Entry entry in _entries.Values)
                if (!entry.Local) counts[entry.Identity] = counts.GetValueOrDefault(entry.Identity) + 1;
            object? largest = null;
            (int Count, long Bytes) most = default;
            foreach ((object source, int count) in counts)
            {
                (int, long) size = (count, _sourceBytes[source]);
                if (size.CompareTo(most) > 0) (largest, most) = (source, size);
            }
            if (largest is null || Equals(largest, owner)) return false;
            foreach (ValueHash256 hash in _order)
            {
                Entry entry = _entries[hash];
                if (entry.Local || !Equals(entry.Identity, largest)) continue;
                RemoveLocked(hash);
                break;
            }
        }
        return true;
    }

    /// <summary>Whether a held raw wrapper lists <paramref name="transaction"/>.</summary>
    public bool Holds(in ValueHash256 transaction)
    {
        lock (_gate) return _byTransaction.ContainsKey(transaction);
    }

    /// <summary>The held raw wrappers, oldest first.</summary>
    public List<Entry> Snapshot()
    {
        lock (_gate)
        {
            List<Entry> entries = new(_entries.Count);
            foreach (ValueHash256 hash in _order) entries.Add(_entries[hash]);
            return entries;
        }
    }

    public bool Remove(in ValueHash256 wrapper)
    {
        lock (_gate) return RemoveLocked(wrapper);
    }

    /// <summary>Drops every held wrapper listing one of <paramref name="transactions"/>.</summary>
    public int RemoveListing(IEnumerable<ValueHash256> transactions)
    {
        int removed = 0;
        lock (_gate)
            foreach (ValueHash256 transaction in transactions)
                if (_byTransaction.TryGetValue(transaction, out List<ValueHash256>? holders))
                    foreach (ValueHash256 wrapper in holders.ToArray())
                        if (RemoveLocked(wrapper)) removed++;
        return removed;
    }

    /// <summary>Keeps the held wrappers <paramref name="keep"/> accepts, for example on a verifier change.</summary>
    public int Retain(Func<Entry, bool> keep)
    {
        List<Entry> entries = Snapshot();
        int removed = 0;
        foreach (Entry entry in entries)
            if (!keep(entry) && Remove(entry.Hash)) removed++;
        return removed;
    }

    /// <summary>Drops candidates past <see cref="MaxAge"/> and those with a transaction that expired or became stale.</summary>
    /// <remarks>The validation prefix is not rerun here: a candidate is simulated on admission and again by its prover.</remarks>
    public int Revalidate()
    {
        lock (_gate) PruneExpired();
        return Retain(entry =>
        {
            foreach (Transaction transaction in entry.Decoded.Transactions)
                if (CheckTransaction(transaction, simulate: false, local: false, out _) is not null) return false;
            return true;
        });
    }

    private void PruneExpired()
    {
        long now = _clock.GetTimestamp();
        while (_order.First is { } oldest && _clock.GetElapsedTime(_entries[oldest.Value].Added, now) > MaxAge)
            RemoveLocked(oldest.Value);
    }

    private bool RemoveLocked(in ValueHash256 wrapper)
    {
        if (!_entries.Remove(wrapper, out Entry? entry)) return false;
        if (entry.Local) _localCount--;
        _order.Remove(entry.Hash);
        _bytes -= entry.Wrapper.Length;
        object owner = entry.Identity;
        long sourceBytes = _sourceBytes[owner] - entry.Wrapper.Length;
        if (sourceBytes == 0) _sourceBytes.Remove(owner);
        else _sourceBytes[owner] = sourceBytes;
        foreach (Transaction transaction in entry.Decoded.Transactions)
        {
            ValueHash256 transactionHash = transaction.Hash!.ValueHash256;
            List<ValueHash256> holders = _byTransaction[transactionHash];
            holders.Remove(wrapper);
            if (holders.Count == 0) _byTransaction.Remove(transactionHash);
        }
        return true;
    }

    /// <summary>Drops the held wrappers listing the newly pending transaction whose transactions are now all pending.</summary>
    private void OnNewPending(object? sender, TxEventArgs e)
    {
        if (e.Transaction.Hash is not { } pending) return;
        List<Entry> listing = [];
        lock (_gate)
            if (_byTransaction.TryGetValue(pending.ValueHash256, out List<ValueHash256>? holders))
                foreach (ValueHash256 wrapper in holders) listing.Add(_entries[wrapper]);
        foreach (Entry entry in listing)
        {
            bool covered = true;
            foreach (Transaction transaction in entry.Decoded.Transactions)
                covered &= transaction.Hash == pending || _txPool.TryGetPendingTransaction(transaction.Hash!.ValueHash256, out _);
            if (covered) Remove(entry.Hash);
        }
    }

    /// <summary>Revalidates off the pool's head loop, one pass at a time.</summary>
    private void OnHeadChanged(object? sender, Block block)
    {
        if (Interlocked.Exchange(ref _revalidating, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { Revalidate(); }
            catch (Exception exception) { if (_logger.IsWarn) _logger.Warn($"EIP-8437 raw candidate revalidation failed: {exception.Message}"); }
            finally { Volatile.Write(ref _revalidating, 0); }
        });
    }

    public void Dispose()
    {
        if (!IsEnabled) return;
        _txPool.NewPending -= OnNewPending;
        _txPool.TxPoolHeadChanged -= OnHeadChanged;
    }
}
