// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    /// <summary>Native raw-signature checks per second, in total and per source; each refills to one second's worth.</summary>
    public const double MaxVerificationsPerSecond = 64;
    public const double MaxVerificationsPerSourcePerSecond = 16;
    /// <summary>How long a candidate waits for a prover before it is dropped.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public enum Admission { Added, Known, Full }

    /// <summary>A held raw wrapper.</summary>
    public sealed class Entry(byte[] wrapper, ValueHash256 hash, MempoolWrapper decoded, Eip8288Verifier verifier, object? source, long added)
    {
        public byte[] Wrapper { get; } = wrapper;
        public ValueHash256 Hash { get; } = hash;
        public MempoolWrapper Decoded { get; } = decoded;

        /// <summary>The verifier the wrapper was validated under.</summary>
        public Eip8288Verifier Verifier { get; internal set; } = verifier;
        internal object? Source { get; } = source;
        internal long Added { get; } = added;

        /// <summary>Whether a prover has tried this candidate, so it is not retried every cycle.</summary>
        public bool Attempted { get; set; }
    }

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
            if (amount > _available) return false;
            _available -= amount;
            return true;
        }
    }

    private static readonly object LocalSource = new();

    private readonly ITxPool _txPool;
    private readonly IChainHeadInfoProvider _head;
    private readonly IFrameTxPrefixSimulator? _simulator;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<ValueHash256, Entry> _entries = [];
    private readonly LinkedList<ValueHash256> _order = new();
    private readonly Dictionary<ValueHash256, List<ValueHash256>> _byTransaction = [];
    private readonly Dictionary<object, long> _sourceBytes = [];
    private readonly Allowance _verifications;
    private readonly ConditionalWeakTable<object, Allowance> _sourceVerifications = [];

    public RawWitnessPool(ITxPool txPool, IChainHeadInfoProvider head, ITxPoolConfig config, IFrameTxPrefixSimulator? simulator = null,
        TimeProvider? clock = null)
    {
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

    public long Bytes { get; private set; }

    /// <summary>Takes <paramref name="count"/> native checks from the global and the source's verification budgets.</summary>
    /// <returns>False when either is spent, which permits refusal without penalty.</returns>
    public bool TryTakeVerifications(object? source, int count)
    {
        lock (_gate)
        {
            Allowance perSource = _sourceVerifications.GetValue(source ?? LocalSource, _ => new(MaxVerificationsPerSourcePerSecond, _clock));
            if (!_verifications.TryTake(count)) return false;
            if (perSource.TryTake(count)) return true;
            _verifications.TryTake(-count);
            return false;
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
    public Admission TryAdd(byte[] wrapper, in ValueHash256 hash, MempoolWrapper decoded, Eip8288Verifier verifier, object? source)
    {
        object owner = source ?? LocalSource;
        lock (_gate)
        {
            if (_entries.ContainsKey(hash)) return Admission.Known;
            PruneExpired();
            _sourceBytes.TryGetValue(owner, out long sourceBytes);
            if (_entries.Count >= MaxWrappers || Bytes + wrapper.Length > MaxBytes || sourceBytes + wrapper.Length > MaxBytesPerSource)
                return Admission.Full;
            Entry entry = new(wrapper, hash, decoded, verifier, source, _clock.GetTimestamp());
            _entries.Add(hash, entry);
            _order.AddLast(hash);
            Bytes += wrapper.Length;
            _sourceBytes[owner] = sourceBytes + wrapper.Length;
            foreach (Transaction transaction in decoded.Transactions)
            {
                ValueHash256 transactionHash = transaction.Hash!.ValueHash256;
                if (!_byTransaction.TryGetValue(transactionHash, out List<ValueHash256>? holders)) _byTransaction[transactionHash] = holders = [];
                holders.Add(hash);
            }
            return Admission.Added;
        }
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
        _order.Remove(entry.Hash);
        Bytes -= entry.Wrapper.Length;
        object owner = entry.Source ?? LocalSource;
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

    private void OnNewPending(object? sender, TxEventArgs e)
    {
        if (e.Transaction.Hash is { } hash) RemoveListing([hash.ValueHash256]);
    }

    private void OnHeadChanged(object? sender, Block block) => Revalidate();

    public void Dispose()
    {
        if (!IsEnabled) return;
        _txPool.NewPending -= OnNewPending;
        _txPool.TxPoolHeadChanged -= OnHeadChanged;
    }
}
