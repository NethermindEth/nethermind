// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Validators;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Shared proof-wrapper validation, admission and aggregation for RPC and eth/73 peers.</summary>
/// <remarks>
/// Wrappers are validated under their target fork, which fixes the verifier: the fork active at the next slot's timestamp,
/// predicted from the local head with the local slot-length estimate (see <see cref="WrapperTargetFork"/>).
/// When the local head advances to a target fork whose verifier differs, the local verifier-change rule applies: every held
/// aggregate proof is dropped, direct witnesses are kept only if they re-validate, and pending transactions left without
/// proof coverage leave the pool.
/// </remarks>
/// <remarks>A blob-carrying transaction arrives with its blob data elided and is handed to <c>blobAdmission</c>, which admits it
/// once EIP-8070 cell retrieval establishes availability; its dependency coverage stays pinned until then.</remarks>
public sealed class ProofWrapperService(ITxPool txPool, ISpecProvider specProvider,
    IBlockFinder blockFinder, LeanProofStore leanProofStore, ILeanProofVerifier leanProofVerifier,
    IWrappedBlobTransactionAdmission? blobAdmission = null)
{
    /// <summary>Encoded transaction bytes a produced mode-1 wrapper may carry: <c>MAX_WRAPPER_BYTES</c> less the largest
    /// aggregate proof, the widest dependency list the pinned guest proves and the list framing.</summary>
    internal const int MaxWrapperTransactionBytes = Eip8288Constants.MaxWrapperBytes - Eip8288Constants.MaxAggregateProofBytes
        - Eip8288Constants.MaxProofDependencies * (2 + Eip8288Constants.DependencyTripleLength) - 64;

    /// <summary>Estimated proving cost (<see cref="RecursiveStarkAggregator.EstimatedCost"/>) one produced wrapper may take: that of
    /// folding <c>MAX_LEANSIG_DEPS_PER_WRAPPER</c> fresh signatures.</summary>
    /// <remarks>EIP-8288 limits mode 1 only by <c>MAX_WRAPPER_BYTES</c> and the circuit's dependency-list limit, here
    /// <see cref="Eip8288Constants.MaxProofDependencies"/>, so a produced wrapper is bounded by those and this budget: it may cover many more dependencies
    /// when they come from already proven aggregates, whose merge costs far less than fresh signatures. A transaction exceeding it
    /// alone is produced in a wrapper of its own.</remarks>
    internal const long MaxWrapperProvingCost = Eip8288Constants.MaxLeanSigDepsPerWrapper * RecursiveStarkAggregator.SignatureCost
        + (Eip8288Constants.MaxLeanSigDepsPerWrapper / RecursiveStarkAggregator.DirectBatchSize - 1) * RecursiveStarkAggregator.MergeCost;

    /// <summary>How long aggregation leaves a dependency set a peer offers a proof of, before proving it itself.</summary>
    internal static readonly TimeSpan ReuseOfferWait = TimeSpan.FromMilliseconds(2 * Eip8288Constants.AggregationInterval);

    /// <summary>Maximal base-fee decreases a pending transaction may still need before it is aggregated.</summary>
    /// <remarks>Aggregation covers only transactions executable at the head, plus those whose fees reach the next block's base
    /// fee after this many decreases of <c>1/BASE_FEE_MAX_CHANGE_DENOMINATOR</c>, (7/8)^2 = 49/64 of it on mainnet, and its
    /// EIP-4844 blob base fee after as many blocks without blobs, so a transaction about to become includable is ready. The
    /// rest stay pooled with their direct witnesses until they qualify. While the EIP-7918 reserve price binds, blobless blocks
    /// do not lower excess blob gas, so the blob floor is lenient there: it may aggregate, never exclude, a few extra transactions.</remarks>
    internal const int MaxAwaitedBaseFeeDecreases = 2;

    /// <summary>How long a dependency set that waited for an offer is proven without waiting again.</summary>
    private static readonly TimeSpan OfferWaitMemory = TimeSpan.FromMinutes(1);

    /// <summary>Whether a peer announced a mode-1 wrapper this node is still fetching whose EIP-8437 dependency digest, Keccak-256
    /// of the concatenated dependency triples, is the given one.</summary>
    /// <remarks>EIP-8288 proof reuse: aggregation leaves such a set for up to <see cref="ReuseOfferWait"/>, so the fetched proof can
    /// be reused instead of generating a new one.</remarks>
    public Func<ValueHash256, bool>? IsDependencySetOffered { get; set; }

    /// <summary>Transactions submitted to the pool per turn of admission.</summary>
    /// <remarks>EIP-8288 bounds a wrapper only by <c>MAX_WRAPPER_BYTES</c>, so one wrapper may carry thousands of small transactions
    /// sharing a dependency. Between chunks admission is handed to the next waiting caller, so such a wrapper cannot hold it
    /// for its whole length.</remarks>
    internal const int AdmissionChunk = 64;

    private const int MaxRememberedVerifications = 128;
    private readonly object _aggregationLock = new();
    private ValueHash256? _resumeAfter;
    private readonly Dictionary<ValueHash256, long> _offerWaits = [];
    private readonly SemaphoreSlim _admission = new(1, 1);

    /// <summary>Callers of one source waiting for admission. Each source enters admission one caller at a time, so RPC callers
    /// cannot crowd peers out of it, nor peers RPC callers.</summary>
    private sealed class AdmissionQueue
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public int Callers;
    }

    private readonly AdmissionQueue _rpcAdmission = new();
    private readonly AdmissionQueue _peerAdmission = new();

    /// <summary>Callers of one source that may wait for admission while another wrapper is admitted.</summary>
    internal int MaxAdmissionWaiters { get; set; } = 16;

    /// <summary>How long a waiting caller waits for admission before it is told to retry.</summary>
    internal TimeSpan AdmissionWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);
    // Remembered verifications are keyed by AGGREGATED_VK too, so none answers under another verifier.
    private readonly Dictionary<(ValueHash256 Wrapper, ValueHash256 Verifier), string?> _verifiedWrappers = [];
    private readonly Queue<(ValueHash256 Wrapper, ValueHash256 Verifier)> _verifiedWrapperOrder = [];
    private sealed record WrapperSnapshot(ValueHash256 Selection, Hash256[] Transactions, byte[] Encoded, ValueHash256 Hash, RecursiveProofInput Parent,
        Eip8288Verifier Verifier);
    private WrapperSnapshot? _cachedWrapper;
    private readonly Lock _verifierLock = new();
    private readonly Lock _commitLock = new();
    internal const string WiderThanCircuitBound = "Mode 1 proof covers more dependencies than the AGGREGATED_VK circuit accepts.";
    private const string VerifierChangedDuringAdmission = "The target fork's verifier changed during validation; retry.";
    private Eip8288Verifier? _verifier;
    private TargetForks? _targetForks;

    /// <summary>The target fork and the fork after it, predicted from <paramref name="Head"/>.</summary>
    private sealed record TargetForks(Hash256? Head, IReleaseSpec Target, IReleaseSpec Following);

    /// <summary>Verifies a wrapper and admits its proof-backed transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => (await AcceptDetailedAsync(wrapper, cancellationToken)).Result;

    /// <summary>Raised with the encoded wrapper once its proof is valid, the pool holds every transaction it carries or newly
    /// admitted at least as many as it rejected (and at least one), and the verifier it was validated under is still current.</summary>
    /// <remarks>EIP-8288: pool rejection of some transactions does not invalidate the wrapper or prevent its unchanged relay. The
    /// second argument is the verifier it was validated under. Subscribers must not mutate the wrapper.</remarks>
    public event Action<byte[], Eip8288Verifier>? WrapperValidated;

    /// <summary>Raised with the new verifier after the verifier-change rule dropped what the previous one validated.</summary>
    public event Action<Eip8288Verifier>? VerifierChanged;

    /// <summary>The fork active at the next slot's timestamp after the local head: the target fork of every wrapper this node
    /// validates.</summary>
    public IReleaseSpec? TargetSpec => blockFinder.Head is { } head ? TargetForksOf(head.Header).Target : null;

    private TargetForks TargetForksOf(BlockHeader head)
    {
        TargetForks? cached = Volatile.Read(ref _targetForks);
        if (cached is not null && head.Hash is not null && cached.Head == head.Hash) return cached;
        BlockHeader? parent = head.Number == 0 || head.ParentHash is null ? null : blockFinder.FindParentHeader(head, BlockTreeLookupOptions.None);
        TargetForks forks = new(head.Hash, WrapperTargetFork.Predict(specProvider, head, parent), WrapperTargetFork.Predict(specProvider, head, parent, 2));
        if (head.Hash is not null) Volatile.Write(ref _targetForks, forks);
        return forks;
    }

    /// <summary>The verifier the verifier-change rule last applied, read without refreshing it.</summary>
    public Eip8288Verifier? CurrentVerifier
    {
        get { lock (_verifierLock) return _verifier; }
    }

    /// <summary>
    /// The verifier of the current target fork, after applying the local verifier-change rule if it changed; null while EIP-8288 is
    /// inactive at the target fork.
    /// </summary>
    /// <remarks>The head is read under the verifier lock, so a caller holding a stale head cannot move the verifier back.</remarks>
    public Eip8288Verifier? RefreshVerifier()
    {
        Eip8288Verifier current;
        Eip8288Verifier? previous;
        lock (_verifierLock)
        {
            if (blockFinder.Head is not { } head) return null;
            IReleaseSpec spec = TargetForksOf(head.Header).Target;
            if (!spec.IsEip8288Enabled) return null;
            current = Eip8288Verifier.For(spec);
            if (_verifier == current) return current;
            previous = _verifier;
            _verifier = current;
            if (previous is null) return current;
        }
        try
        {
            ApplyUpgradeRule(current);
        }
        catch
        {
            // A half-applied verifier-change rule must run again on the next refresh rather than be skipped as already applied.
            lock (_verifierLock)
                if (_verifier == current) _verifier = previous;
            throw;
        }
        return current;
    }

    /// <summary>Whether a held wrapper re-validates under <paramref name="verifier"/>: a mode-0 wrapper whose direct witnesses
    /// verify under its schemes' parameters. A mode-1 proof never verifies under another <c>AGGREGATED_VK</c>, so it never does.</summary>
    public bool Revalidates(byte[] wrapper, in Eip8288Verifier verifier)
    {
        MempoolWrapper decoded;
        try { decoded = DecodeProofWrapper(wrapper); }
        catch (Exception exception) when (exception is RlpException or ArgumentException) { return false; }
        return decoded.Mode == MempoolWrapper.ModeDirect && MempoolWrapperValidator.VerifyProofs(decoded, leanProofVerifier, in verifier, out _);
    }

    /// <summary>Local verifier-change rule: drops every aggregate proof, keeps direct witnesses only if they re-validate under
    /// <paramref name="verifier"/>, and evicts pending transactions no longer covered.</summary>
    private void ApplyUpgradeRule(Eip8288Verifier verifier)
    {
        (int dropped, int evicted) = Purge(verifier);
        TxPool.Metrics.Eip8288VerifierChanges++;
        TxPool.Metrics.Eip8288UpgradeDroppedProofs += dropped;
        TxPool.Metrics.Eip8288UpgradeEvictedTransactions += evicted;
        VerifierChanged?.Invoke(verifier);
    }

    /// <summary>Drops what <paramref name="verifier"/> does not validate: every aggregate proof, direct witnesses that do not
    /// re-validate, and pending transactions left uncovered.</summary>
    /// <remarks>Runs under the commit lock, so admission and aggregation either commit before it or see the new verifier.</remarks>
    private (int Dropped, int Evicted) Purge(Eip8288Verifier verifier)
    {
        lock (_commitLock)
        {
            Volatile.Write(ref _cachedWrapper, null);
            int dropped = leanProofStore.RetainDirect((dependency, witness) => ProofVerdictCache.VerifyDirect(leanProofVerifier, in verifier, dependency, witness));
            int evicted = 0;
            foreach (Transaction transaction in txPool.GetPendingTransactions())
                if (Eip8288Dependencies.IsDependencyBearing(transaction) && !leanProofStore.Covers(transaction) && txPool.RemoveTransaction(transaction.Hash))
                    evicted++;
            // The blob pool's light records carry no frames, so coverage is judged on the full transaction.
            foreach (Transaction[] senderTransactions in txPool.GetPendingLightBlobTransactionsBySender().Values)
                foreach (Transaction light in senderTransactions)
                    if (Eip8288Dependencies.IsDependencyBearing(light)
                        && txPool.TryGetPendingTransactionWithoutBlobs(light.Hash!.ValueHash256, out Transaction? full)
                        && !leanProofStore.Covers(full) && txPool.RemoveTransaction(light.Hash))
                        evicted++;
            return (dropped, evicted);
        }
    }

    /// <summary>Separates malformed wrappers and invalid proofs from ordinary pool admission failures.</summary>
    /// <remarks>For local submissions, such as RPC; they wait for admission apart from peers. Transactions are admitted in chunks,
    /// so admission that times out waiting for its next turn, or is cancelled, after a chunk was pooled reports
    /// <see cref="ProofWrapperAcceptanceStatus.Interrupted"/>: the pooled transactions stay, and resubmitting the same wrapper is
    /// safe, as it skips them.</remarks>
    public Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => AcceptDetailedCoreAsync(wrapper, true, cancellationToken);

    /// <summary>Accepts an exclusively owned transport buffer without another encoded-body copy.</summary>
    /// <remarks>The transport must retain its charged buffer throughout the returned task and must not mutate it.
    /// Validation subscribers must treat the encoded body as read-only.</remarks>
    /// <param name="source">The connection the wrapper came from, to which cell retrieval for its blob-carrying transactions is
    /// first attributed.</param>
    internal Task<ProofWrapperAcceptance> AcceptOwnedDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default, object? source = null)
        => AcceptDetailedCoreAsync(wrapper, false, cancellationToken, source);

    private async Task<ProofWrapperAcceptance> AcceptDetailedCoreAsync(byte[] wrapper, bool copyInput, CancellationToken cancellationToken,
        object? source = null)
    {
        IReleaseSpec? admissionSpec = TargetSpec;
        if (admissionSpec?.IsEip8288Enabled != true)
            return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
        if (wrapper.Length > Eip8288Constants.MaxWrapperBytes)
            return ProofWrapperAcceptance.Malformed("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        AdmissionQueue queue = copyInput ? _rpcAdmission : _peerAdmission;
        if (Interlocked.Increment(ref queue.Callers) > MaxAdmissionWaiters + 1)
        {
            Interlocked.Decrement(ref queue.Callers);
            return AdmissionBusy();
        }
        bool entered = false;
        try { entered = await EnterAdmissionAsync(queue, cancellationToken); }
        finally { if (!entered) Interlocked.Decrement(ref queue.Callers); }
        if (!entered) return AdmissionBusy();
        try
        {
            // Inside the try, so a verifier-change-rule fault is a local failure rather than an exception escaping to the caller.
            if (RefreshVerifier() is not { } verifier) return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
            cancellationToken.ThrowIfCancellationRequested();
            if (copyInput) wrapper = (byte[])wrapper.Clone();
            MempoolWrapper decoded;
            try { decoded = DecodeProofWrapper(wrapper); }
            catch (Exception exception) when (exception is RlpException or ArgumentException)
            {
                return ProofWrapperAcceptance.Malformed($"Invalid proof wrapper RLP: {exception.Message}");
            }
            // EIP-8288: mode 1 is limited by the dependency list the AGGREGATED_VK circuit accepts, here the pinned guest's.
            if (decoded.Mode == MempoolWrapper.ModeRecursive && !Eip8288Dependencies.FitsProofList(decoded.Deps))
                return ProofWrapperAcceptance.Invalid(WiderThanCircuitBound, verifier);
            // EIP-8437 carries blob-carrying transactions with their blob data elided, so a wrapper submitted with full blobs is
            // relayed in that form; its blobs reach peers as EIP-8070 cells once the pool holds them.
            byte[]? relay = ElidedForRelay(decoded, wrapper);
            foreach (Transaction transaction in decoded.Transactions)
            {
                if (!Preflight(transaction, out string? frameError, admissionSpec))
                    return frameError is FrameTxValidation.AssertFrameNotEnabled or FrameTxValidation.KeyedNoncesNotEnabled
                        or FrameTxValidation.LegacyNonceNotAllowed
                        ? ProofWrapperAcceptance.LocalFailure(frameError) : ProofWrapperAcceptance.Invalid(frameError!, verifier);
            }
            (ValueHash256, ValueHash256) wrapperHash = (ValueKeccak.Compute(wrapper), verifier.AggregatedVk);
            if (!_verifiedWrappers.TryGetValue(wrapperHash, out string? error))
            {
                (bool valid, string? validationError) = await Task.Run(() =>
                {
                    bool proofValid = MempoolWrapperValidator.Validate(decoded, leanProofVerifier, in verifier, out string? proofError);
                    return (proofValid, proofError);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                error = validationError;
                RememberVerification(wrapperHash, valid ? null : error);
                if (!valid) return ProofWrapperAcceptance.Invalid(error!, verifier);
            }
            else if (error is not null) return ProofWrapperAcceptance.Invalid(error, verifier);
            // Nothing validated under a verifier the verifier-change rule has since replaced may be published or admitted.
            if (RefreshVerifier() != verifier) return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
            // Coverage keeps one record per dependency, so a verified recursive proof can otherwise be lost to a direct
            // witness or eviction; indexed by its exact set, it stays reusable by producers restricted to proven sets. The
            // verifier-change rule purges under the commit lock, so a proof cached here never outlives the verifier it was checked under.
            if (decoded.RecursiveStark is { } recursive)
            {
                lock (_commitLock)
                {
                    if (RefreshVerifier() != verifier) return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
                    leanProofStore.AddCachedRecursive(decoded.Deps, recursive.StarkProof);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            List<FrameDependency> admittedDependencies = [];
            Result<Hash256[]> admission;
            IDisposable? scope;
            int pooled = 0, admitted = 0;
            lock (_commitLock)
            {
                if (RefreshVerifier() != verifier) return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
                if (!TryBeginAdmission(decoded, out scope))
                    return ProofWrapperAcceptance.LocalFailure("Proof witness capacity is full; retry later.");
            }
            using (scope)
            {
                IReadOnlyList<Transaction> transactions = decoded.Transactions;
                Hash256[] hashes = new Hash256[transactions.Count];
                string? firstError = null;
                try
                {
                    for (int start = 0; start < transactions.Count; start += AdmissionChunk)
                    {
                        if (start != 0)
                        {
                            // Coverage of the chunks already in the pool is published before admission is handed on.
                            leanProofStore.CommitAdmission(admittedDependencies);
                            admittedDependencies.Clear();
                            entered = false;
                            ExitAdmission(queue);
                            if (!(entered = await EnterAdmissionAsync(queue, cancellationToken)))
                                return pooled == 0 ? AdmissionBusy() : Interrupted(pooled, transactions.Count);
                            Eip8288Verifier? refreshed = RefreshVerifier();
                            if (refreshed != verifier)
                            {
                                if (refreshed is { } changed) Purge(changed);
                                return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
                            }
                        }
                        SubmitProofTransactions(transactions, start, Math.Min(start + AdmissionChunk, transactions.Count), hashes,
                            ref firstError, ref pooled, ref admitted, cancellationToken, admittedDependencies, source,
                            copyInput ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && pooled != 0)
                {
                    return Interrupted(pooled, transactions.Count);
                }
                finally
                {
                    leanProofStore.CommitAdmission(admittedDependencies);
                }
                admission = firstError is null ? Result<Hash256[]>.Success(hashes) : Result<Hash256[]>.Fail(firstError);
            }
            // A verifier change during pool insertion purged before this admission committed; purge its leftovers.
            if (RefreshVerifier() is { } current && current != verifier) Purge(current);
            // Already pending transactions are free to include, so only newly admitted ones may carry rejected ones, at most one each.
            int rejected = decoded.Transactions.Count - pooled;
            if ((admission.IsSuccess || admitted != 0 && rejected <= admitted) && relay is not null && RefreshVerifier() == verifier)
                WrapperValidated?.Invoke(relay, verifier);
            return new(admission.IsSuccess ? ProofWrapperAcceptanceStatus.Accepted : ProofWrapperAcceptanceStatus.PoolRejected, admission);
        }
        catch (RlpException)
        {
            return ProofWrapperAcceptance.LocalFailure("Local proof admission failed.");
        }
        catch (ArgumentException exception)
        {
            return ProofWrapperAcceptance.LocalFailure(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ProofWrapperAcceptance.LocalFailure(exception.Message);
        }
        finally
        {
            if (entered) ExitAdmission(queue);
            Interlocked.Decrement(ref queue.Callers);
        }
    }

    /// <summary>Reserves the wrapper's witnesses, first evicting pending transactions not executable at the head when proof
    /// capacity is full.</summary>
    /// <remarks>Witnesses pinned by pending transactions are never evicted, so a full store would otherwise refuse every wrapper.
    /// Transactions the next blocks could not include give way, oldest first, to a wrapper whose new transactions the next blocks
    /// could, but only when evicting at most <see cref="MaxEvictedPerAdmission"/> of them frees enough, and only those whose
    /// eviction frees witness capacity.</remarks>
    private bool TryBeginAdmission(MempoolWrapper decoded, out IDisposable? scope)
    {
        if (leanProofStore.TryBeginAdmission(decoded.Deps, decoded.Proofs, decoded.RecursiveStark?.StarkProof, out scope)) return true;
        FeeFloors floors = ExecutableFeeFloors();
        if (!AddsOnlyExecutable(decoded.Transactions, floors)) return false;
        List<Transaction> evictable = NotExecutable(decoded.Transactions, floors);
        if (evictable.Count == 0) return false;
        ValueHash256[] hashes = new ValueHash256[Math.Min(evictable.Count, MaxEvictedPerAdmission)];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = evictable[i].Hash!.ValueHash256;
        if (leanProofStore.ReleasesForAdmission(decoded.Deps, decoded.Proofs, decoded.RecursiveStark?.StarkProof, hashes)
            is not { Count: > 0 } releases) return false;
        foreach (ValueHash256 hash in releases)
            if (txPool.RemoveTransaction(new Hash256(hash))) TxPool.Metrics.Eip8288CapacityEvictedTransactions++;
        HashSet<ValueHash256> released = [.. releases];
        _evictionCandidates?.Candidates.RemoveAll(transaction => released.Contains(transaction.Hash!.ValueHash256));
        return leanProofStore.TryBeginAdmission(decoded.Deps, decoded.Proofs, decoded.RecursiveStark?.StarkProof, out scope);
    }

    /// <summary>Pending transactions one admission may evict to make room for its witnesses.</summary>
    internal const int MaxEvictedPerAdmission = AdmissionChunk;

    /// <summary>Hashes of transactions admitted through local wrappers, such as RPC, which capacity eviction removes last.</summary>
    /// <remarks>Bounded by <see cref="MaxLocalTransactions"/>, beyond which local admissions are not remembered, and pruned to the
    /// pending ones on each pool scan.</remarks>
    private readonly HashSet<ValueHash256> _localTransactions = [];

    internal const int MaxLocalTransactions = 4096;

    private void MarkLocal(Hash256 hash)
    {
        lock (_localTransactions)
            if (_localTransactions.Count < MaxLocalTransactions) _localTransactions.Add(hash.ValueHash256);
    }

    /// <summary>The pool's dependency-bearing transactions not executable at the head, oldest first, scanned once per head and
    /// read and replaced under the commit lock.</summary>
    /// <remarks>Transactions removed since remain listed; the store does not count them as releasing anything.</remarks>
    private (Hash256 Head, List<Transaction> Candidates)? _evictionCandidates;

    /// <summary>Pending dependency-bearing transactions not executable at the head, other than <paramref name="wrapped"/>'s, oldest
    /// first, with those admitted locally after all others.</summary>
    private List<Transaction> NotExecutable(IReadOnlyList<Transaction> wrapped, FeeFloors floors)
    {
        if (blockFinder.Head?.Hash is not { } head) return [];
        if (_evictionCandidates is not { } cached || cached.Head != head)
            _evictionCandidates = cached = (head, ScanNotExecutable(floors));
        HashSet<Hash256> skipped = [];
        foreach (Transaction transaction in wrapped) skipped.Add(transaction.Hash!);
        List<Transaction> notExecutable = [], local = [];
        lock (_localTransactions)
            foreach (Transaction transaction in cached.Candidates)
                if (!skipped.Contains(transaction.Hash!))
                    (_localTransactions.Contains(transaction.Hash!.ValueHash256) ? local : notExecutable).Add(transaction);
        notExecutable.AddRange(local);
        return notExecutable;
    }

    /// <summary>The pool's dependency-bearing transactions not executable at the head, oldest first.</summary>
    /// <remarks>Forgets local transactions no longer pending.</remarks>
    private List<Transaction> ScanNotExecutable(FeeFloors floors)
    {
        List<Transaction> notExecutable = [];
        List<Transaction> executable = [];
        HashSet<ValueHash256> pending = [];
        Collect(txPool.GetPendingTransactionsBySender());
        Collect(txPool.GetPendingLightBlobTransactionsBySender());
        lock (_localTransactions) _localTransactions.IntersectWith(pending);
        notExecutable.Sort(static (x, y) => x.Timestamp.CompareTo(y.Timestamp));
        return notExecutable;

        void Collect(IDictionary<AddressAsKey, Transaction[]> bySender)
        {
            foreach ((AddressAsKey sender, Transaction[] transactions) in bySender)
            {
                executable.Clear();
                AddExecutable(sender, transactions, floors, executable);
                foreach (Transaction transaction in transactions)
                {
                    pending.Add(transaction.Hash!.ValueHash256);
                    if (Eip8288Dependencies.IsDependencyBearing(transaction) && !executable.Contains(transaction))
                        notExecutable.Add(transaction);
                }
            }
        }
    }

    /// <summary>Whether <paramref name="wrapped"/> adds transactions to the pool and each it adds would be executable at the head:
    /// fees of at least <paramref name="floors"/> and account nonces continuing the sender's pending ones.</summary>
    /// <remarks>An EIP-8250 keyed transaction is never judged executable here, as its nonce lives in state this does not read.</remarks>
    private bool AddsOnlyExecutable(IReadOnlyList<Transaction> wrapped, in FeeFloors floors)
    {
        List<Transaction> added = [];
        foreach (Transaction transaction in wrapped)
        {
            if (txPool.TryGetPendingTransaction(transaction.Hash!.ValueHash256, out _)) continue;
            if (KeyedNonceManager.UsesKeyedNonce(transaction) || !IsPriced(transaction, floors)) return false;
            added.Add(transaction);
        }
        added.Sort(static (x, y) => x.SenderAddress!.Bytes.SequenceCompareTo(y.SenderAddress!.Bytes) is var bySender && bySender != 0
            ? bySender : x.Nonce.CompareTo(y.Nonce));
        Address? sender = null;
        ulong next = 0;
        foreach (Transaction transaction in added)
        {
            if (transaction.SenderAddress != sender) next = txPool.GetLatestPendingNonce(sender = transaction.SenderAddress!);
            if (transaction.Nonce != next++) return false;
        }
        return added.Count != 0;
    }

    /// <summary>Takes the source's turn and then admission, within <see cref="AdmissionWaitTimeout"/>.</summary>
    private async Task<bool> EnterAdmissionAsync(AdmissionQueue queue, CancellationToken cancellationToken)
    {
        bool hasTurn = false, entered = false;
        try
        {
            long started = Stopwatch.GetTimestamp();
            hasTurn = await queue.Turn.WaitAsync(AdmissionWaitTimeout, cancellationToken);
            TimeSpan remaining = AdmissionWaitTimeout - Stopwatch.GetElapsedTime(started);
            entered = hasTurn && await _admission.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
            return entered;
        }
        finally { if (hasTurn && !entered) queue.Turn.Release(); }
    }

    private void ExitAdmission(AdmissionQueue queue)
    {
        _admission.Release();
        queue.Turn.Release();
    }

    private static ProofWrapperAcceptance Interrupted(int pooled, int count) =>
        new(ProofWrapperAcceptanceStatus.Interrupted, Result<Hash256[]>.Fail(
            $"Proof admission stopped with {pooled} of {count} transactions pooled; resubmitting the wrapper is safe, as pooled ones are skipped."));

    private static ProofWrapperAcceptance AdmissionBusy() =>
        new(ProofWrapperAcceptanceStatus.Busy, Result<Hash256[]>.Fail("Proof admission is busy; retry later."));

    /// <summary>The wrapper as EIP-8437 relays it: unchanged unless a transaction carries blob data, which is elided, leaving
    /// its commitments and cell proofs. Null when a transaction's blobs have no cell proofs, so it cannot be relayed.</summary>
    private static byte[]? ElidedForRelay(MempoolWrapper decoded, byte[] wrapper)
    {
        Transaction[]? elided = null;
        for (int i = 0; i < decoded.Transactions.Count; i++)
        {
            Transaction transaction = decoded.Transactions[i];
            if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper { Blobs.Length: > 0 } blobs) continue;
            if (blobs.Version != ProofVersion.V1) return null;
            elided ??= [.. decoded.Transactions];
            Transaction copy = new();
            transaction.CopyTo(copy, copyHash: true);
            copy.NetworkWrapper = blobs with { Blobs = [], CellMask = default, Cells = null };
            copy.ClearLengthCache();
            elided[i] = copy;
        }

        if (elided is null) return wrapper;
        return MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = elided,
            Mode = decoded.Mode,
            Deps = decoded.Deps,
            Proofs = decoded.Proofs,
            RecursiveStark = decoded.RecursiveStark
        }).Bytes;
    }

    private void RememberVerification((ValueHash256, ValueHash256) hash, string? error)
    {
        if (_verifiedWrappers.ContainsKey(hash)) return;
        if (_verifiedWrapperOrder.Count == MaxRememberedVerifications) _verifiedWrappers.Remove(_verifiedWrapperOrder.Dequeue());
        _verifiedWrapperOrder.Enqueue(hash);
        _verifiedWrappers[hash] = error;
    }

    /// <summary>Submits <paramref name="transactions"/> from <paramref name="start"/> up to <paramref name="end"/>.</summary>
    /// <param name="handling">Local submissions persist and bypass the pool's gossip-only filters; peer-served ones do not.</param>
    private void SubmitProofTransactions(IReadOnlyList<Transaction> transactions, int start, int end, Hash256[] hashes, ref string? firstError,
        ref int pooled, ref int admitted, CancellationToken cancellationToken, List<FrameDependency> admittedDependencies, object? source,
        TxHandlingOptions handling)
    {
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transaction transaction = transactions[i];
            Hash256 hash = transaction.Hash!;
            hashes[i] = hash;
            if (txPool.TryGetPendingTransaction(hash.ValueHash256, out _))
            {
                pooled++;
                admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue));
                continue;
            }
            // Admission is per transaction: a pool rejection, including EIP-8272 recent-root mempool rules, leaves the proof and
            // the other transactions intact.
            AcceptTxResult result = IsAwaitingAvailability(transaction)
                ? AdmitAwaitingAvailability(transaction, source)
                : txPool.SubmitTx(transaction, handling);
            if (result != AcceptTxResult.Accepted) firstError ??= result.ToString();
            else
            {
                pooled++;
                admitted++;
                admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue));
                if ((handling & TxHandlingOptions.PersistentBroadcast) != 0) MarkLocal(hash);
            }
        }
    }

    /// <summary>Whether a wrapped transaction carries blobs without their data, as EIP-8437 transports it, so its availability
    /// is established through EIP-8070 cell retrieval rather than from the wrapper.</summary>
    private static bool IsAwaitingAvailability(Transaction transaction) =>
        transaction.CarriesBlobs && transaction.NetworkWrapper is ShardBlobNetworkWrapper { Blobs.Length: 0, Cells: null };

    /// <summary>Pins the transaction's coverage, still inside this wrapper's admission, and hands it to cell retrieval.</summary>
    private AcceptTxResult AdmitAwaitingAvailability(Transaction transaction, object? source)
    {
        if (blobAdmission is null) return AcceptTxResult.Invalid.WithMessage("Blob cell retrieval is unavailable.");
        // A fresh owner per admission, so each pin is released exactly once whoever else pins the same transaction.
        object owner = new();
        if (!leanProofStore.PinPending(transaction, owner)) return AcceptTxResult.MissingDependencyProof;
        return blobAdmission.Admit(transaction, source, new ProofPin(leanProofStore, transaction.Hash!.ValueHash256, owner));
    }

    private sealed class ProofPin(LeanProofStore store, ValueHash256 hash, object owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) store.UnpinPending(hash, owner);
        }
    }

    /// <summary>Builds a recursive wrapper over the current proof-backed pool view.</summary>
    /// <remarks>Fails when no proof-backed transaction is pending, since a wrapper carries at least one.</remarks>
    public Result<byte[]> BuildWrapper(CancellationToken cancellationToken = default)
        => BuildWrapper(true, cancellationToken);

    /// <summary>One <c>AGGREGATION_INTERVAL</c> cycle of background aggregation: builds the wrappers of the hash-prefix buckets
    /// (see <see cref="PlanBuckets"/>), continuing the rotation, until every pending dependency-bearing transaction has been offered
    /// to one, calling <paramref name="produced"/> after each new one, so it can be read through <see cref="GetLatestWrapper()"/>
    /// and published.</summary>
    /// <remarks>Stops at the first build failure, or before starting another build once <paramref name="budget"/> has elapsed,
    /// so prover work per cycle stays bounded; the next cycle resumes the rotation where this one stopped. A bucket whose
    /// dependency set a peer offers is skipped while <see cref="IsDependencySetOffered"/> defers it.</remarks>
    /// <returns>Success when at least one wrapper is current; otherwise the first failure.</returns>
    public Result RefreshWrapper(Action? produced = null, TimeSpan? budget = null, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        clock ??= TimeProvider.System;
        long started = clock.GetTimestamp();
        if (!Monitor.TryEnter(_aggregationLock)) return Result.Fail("Proof aggregation is busy; retry later.");
        try
        {
            using IDisposable request = ProductionProofCache.Background(cancellationToken);
            if (RefreshVerifier() is not { } verifier) return Result.Fail(WrappersUnavailable);
            List<Bucket> buckets = PlanBuckets(verifier, cancellationToken);
            if (buckets.Count == 0) return Result.Fail(NoProofBackedTransactions);
            PruneOfferWaits(clock);
            int start = ResumeIndex(buckets);
            bool any = false;
            string? deferred = null;
            for (int i = 0; i < buckets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i != 0 && budget is { } limit && clock.GetElapsedTime(started) >= limit) break;
                int index = (start + i) % buckets.Count;
                Result<byte[]> result = BuildBucket(buckets[index], verifier, false, clock, cancellationToken, out bool unchanged);
                if (result.Error != ProductionProofCache.BackgroundBusyMessage) Advance(buckets, index);
                if (result.IsSuccess)
                {
                    any = true;
                    if (!unchanged) produced?.Invoke();
                }
                else if (result.Error == AwaitingOfferedProof) deferred = result.Error;
                else return any ? Result.Success : Result.Fail(result.Error!);
            }
            return any ? Result.Success : Result.Fail(deferred ?? NoProofBackedTransactions);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Fail(exception.Message);
        }
        finally { Monitor.Exit(_aggregationLock); }
    }

    private Result<byte[]> BuildWrapper(bool returnEncoded, CancellationToken cancellationToken)
    {
        if (!Monitor.TryEnter(_aggregationLock)) return Result<byte[]>.Fail("Proof aggregation is busy; retry later.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IDisposable request = ProductionProofCache.Background(cancellationToken);
            if (RefreshVerifier() is not { } verifier) return Result<byte[]>.Fail(WrappersUnavailable);
            List<Bucket> buckets = PlanBuckets(verifier, cancellationToken);
            int start = ResumeIndex(buckets);
            Result<byte[]> result = Result<byte[]>.Fail(NoProofBackedTransactions);
            for (int i = 0; i < buckets.Count; i++)
            {
                int index = (start + i) % buckets.Count;
                result = BuildBucket(buckets[index], verifier, returnEncoded, TimeProvider.System, cancellationToken, out _);
                if (result.Error != ProductionProofCache.BackgroundBusyMessage) Advance(buckets, index);
                if (result.Error != AwaitingOfferedProof) break;
            }
            return result;
        }
        catch (InvalidOperationException exception)
        {
            return Result<byte[]>.Fail(exception.Message);
        }
        finally { Monitor.Exit(_aggregationLock); }
    }

    private const string WrappersUnavailable = "EIP-8288 proof wrappers are unavailable.";
    private const string AwaitingOfferedProof = "A peer offers a proof of this dependency set; awaiting it before proving.";

    private const string NoProofBackedTransactions = "No proof-backed pending transactions.";

    /// <summary>Returns the latest background-produced wrapper without invoking the prover.</summary>
    public Result<byte[]> GetLatestWrapper() => GetLatestWrapper(null, out _);

    /// <summary>Rechecks pending membership and returns an empty array when the supplied hash is current.</summary>
    public Result<byte[]> GetLatestWrapper(ValueHash256? knownHash, out ValueHash256 hash)
    {
        hash = default;
        if (!IsEnabled) return Result<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.");
        WrapperSnapshot? wrapper = Volatile.Read(ref _cachedWrapper);
        Eip8288Verifier? verifier;
        // A pending verifier-change rule leaves the cached wrapper unpublishable until it applies.
        try { verifier = RefreshVerifier(); }
        catch (InvalidOperationException) { verifier = null; }
        if (wrapper is null || wrapper.Verifier != verifier || !ArePending(wrapper.Transactions))
            return Result<byte[]>.Fail("Proof wrapper is not ready; retry after the next background cycle.");
        hash = wrapper.Hash;
        return Result<byte[]>.Success(knownHash == hash ? [] : (byte[])wrapper.Encoded.Clone());
    }

    /// <summary>Dependencies and wrapper-encoded length of the transactions the last plan saw, by hash; read and replaced under
    /// the aggregation lock.</summary>
    private Dictionary<ValueHash256, (List<FrameDependency> Deps, int EncodedLength)> _candidateShapes = [];

    /// <summary>A pending dependency-bearing transaction with its dependencies and encoded size.</summary>
    private readonly record struct Candidate(Transaction Transaction, List<FrameDependency> Deps, int EncodedLength);

    /// <summary>A planned wrapper: a hash-prefix bucket of transactions, in canonical order, and the inputs proving their
    /// dependency union.</summary>
    private sealed record Bucket(List<Transaction> Transactions, List<FrameDependency> Deps, AggregationInput Input);

    /// <summary>Partitions the pending dependency-bearing transactions into transaction-hash-prefix buckets that each fit one
    /// mode-1 wrapper.</summary>
    /// <remarks>EIP-8288 proof reuse: the candidates in canonical order start as one bucket, and a bucket exceeding the wrapper's
    /// transaction bytes, the circuit's dependency-list limit or the proving-cost budget is subdivided by successive hash bits.
    /// Buckets do not depend on arrival order, but the cost check depends on the proofs held here, so nodes with the same pool view
    /// share bucket boundaries only where their held proofs agree. A transaction exceeding the proving
    /// budget alone is still a bucket of its own; one whose inputs are unavailable or exceed the circuit is left out.
    /// Only transactions executable at the head are candidates (see <see cref="AddExecutable"/>).</remarks>
    private List<Bucket> PlanBuckets(Eip8288Verifier verifier, CancellationToken cancellationToken)
    {
        WrapperSnapshot? previous = Volatile.Read(ref _cachedWrapper);
        RecursiveProofInput? previousProof = previous is not null && previous.Verifier == verifier ? previous.Parent : null;
        FeeFloors floors = ExecutableFeeFloors();
        List<Transaction> pending = [];
        foreach ((AddressAsKey sender, Transaction[] bucket) in txPool.GetPendingTransactionsBySender())
            AddExecutable(sender, bucket, floors, pending);
        AddBlobCandidates(pending, floors);
        List<Candidate> candidates = new(pending.Count);
        Dictionary<ValueHash256, (List<FrameDependency> Deps, int EncodedLength)> shapes = new(_candidateShapes.Count);
        foreach (Transaction transaction in pending)
        {
            if (!transaction.SupportsFrames) continue;
            ValueHash256 hash = transaction.Hash!.ValueHash256;
            if (!_candidateShapes.TryGetValue(hash, out (List<FrameDependency> Deps, int EncodedLength) shape))
                shape = (Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue),
                    Rlp.LengthOfSequence(TxDecoder.Instance.GetLength(transaction, MempoolWrapperDecoder.TransactionBehaviors)));
            shapes[hash] = shape;
            if (shape.Deps.Count != 0 && shape.EncodedLength <= MaxWrapperTransactionBytes)
                candidates.Add(new(transaction, shape.Deps, shape.EncodedLength));
        }
        // Only transactions still pending are remembered, so removed ones are dropped.
        _candidateShapes = shapes;
        candidates.Sort(static (a, b) => a.Transaction.Hash!.Bytes.SequenceCompareTo(b.Transaction.Hash!.Bytes));
        List<Bucket> buckets = [];
        Plan(0, candidates.Count, 0);
        return buckets;

        void Plan(int start, int end, int bit)
        {
            if (start == end) return;
            cancellationToken.ThrowIfCancellationRequested();
            Bucket? bucket = TryBucket(start, end, out bool fits);
            if (fits || end - start == 1 || bit == 8 * Hash256.Size)
            {
                if (bucket is not null) buckets.Add(bucket);
                return;
            }
            int middle = start;
            while (middle < end && !HasBit(candidates[middle].Transaction.Hash!, bit)) middle++;
            Plan(start, middle, bit + 1);
            Plan(middle, end, bit + 1);
        }

        Bucket? TryBucket(int start, int end, out bool fits)
        {
            fits = false;
            long bytes = 0;
            for (int i = start; i < end; i++) bytes += candidates[i].EncodedLength;
            if (bytes > MaxWrapperTransactionBytes) return null;
            List<FrameDependency> union = [];
            for (int i = start; i < end; i++) union.AddRange(candidates[i].Deps);
            union = Eip8288Dependencies.Canonicalize(union);
            if (!Eip8288Dependencies.FitsProofList(union) || !leanProofStore.TryGetInput(union, out AggregationInput input)
                || union.Count + input.Discards.Count > Eip8288Constants.MaxProofDependencies) return null;
            fits = end - start == 1 || ProvingCost(input, union, previousProof) <= MaxWrapperProvingCost;
            List<Transaction> transactions = new(end - start);
            for (int i = start; i < end; i++) transactions.Add(candidates[i].Transaction);
            return new(transactions, union, input);
        }
    }

    /// <summary>The lowest fees per gas and per blob gas a transaction executable at the head may offer, at <see cref="Spec"/>.</summary>
    private readonly record struct FeeFloors(UInt256 PerGas, UInt256 PerBlobGas, IReleaseSpec? Spec);

    /// <summary>The next block's base fee and blob base fee, each after <see cref="MaxAwaitedBaseFeeDecreases"/> maximal decreases;
    /// zero where the fork does not charge it.</summary>
    /// <remarks>A blob base fee decreases most in a block without blobs, which lowers the excess blob gas by the target.</remarks>
    private FeeFloors ExecutableFeeFloors()
    {
        if (blockFinder.Head is not { } head) return default;
        IReleaseSpec spec = TargetForksOf(head.Header).Target;
        UInt256 fee = spec.IsEip1559Enabled ? BaseFeeCalculator.Calculate(head.Header, spec) : UInt256.Zero;
        if (!spec.BaseFeeMaxChangeDenominator.IsZero)
            for (int i = 0; i < MaxAwaitedBaseFeeDecreases; i++) fee -= fee / spec.BaseFeeMaxChangeDenominator;
        if (BlobGasCalculator.CalculateExcessBlobGas(head.Header, spec) is not { } excess) return new(fee, UInt256.Zero, spec);
        ulong target = spec.GasCosts.TargetBlobGasPerBlock;
        excess = excess > MaxAwaitedBaseFeeDecreases * target ? excess - MaxAwaitedBaseFeeDecreases * target : 0;
        return new(fee, BlobGasCalculator.TryCalculateFeePerBlobGas(excess, spec.BlobBaseFeeUpdateFraction, out UInt256 blobFee)
            ? blobFee : UInt256.MaxValue, spec);
    }

    /// <summary>Adds the frame transactions of <paramref name="sender"/>'s nonce-ordered pending <paramref name="bucket"/> executable
    /// at the head to <paramref name="executable"/>.</summary>
    /// <remarks>Executable means fees of at least <paramref name="floors"/> and no nonce gap from the sender's state nonce,
    /// so an account-nonce transaction also needs every one before it to qualify. An EIP-8250 keyed transaction is judged alone:
    /// the pool holds only keyed transactions at their current sequence.</remarks>
    private void AddExecutable(Address sender, Transaction[] bucket, in FeeFloors floors, List<Transaction> executable)
    {
        if (!Array.Exists(bucket, static transaction => transaction.SupportsFrames)) return;
        ulong? nextNonce = null;
        bool blocked = false;
        foreach (Transaction transaction in bucket)
        {
            bool priced = IsPriced(transaction, floors);
            if (KeyedNonceManager.UsesKeyedNonce(transaction))
            {
                if (!priced) continue;
            }
            else
            {
                nextNonce ??= txPool.GetLatestPendingNonce(sender);
                blocked |= !priced || transaction.Nonce >= nextNonce;
                if (blocked) continue;
            }
            if (transaction.SupportsFrames) executable.Add(transaction);
        }
    }

    // An EIP-8141 max_fee is checked as a budget: its implied per-gas cap leaves out intrinsic gas, recursive_stark_gas included.
    private static bool IsPriced(Transaction transaction, in FeeFloors floors) => transaction.SupportsFrames
        ? floors.Spec is null || FrameTxValidation.CoversRequiredMaxFee(transaction, floors.Spec, floors.PerGas, floors.PerBlobGas)
        : transaction.MaxFeePerGas >= floors.PerGas && transaction.CanPayForBlobGas(floors.PerBlobGas);

    private static bool HasBit(Hash256 hash, int bit) => (hash.Bytes[bit >> 3] >> (7 - (bit & 7)) & 1) != 0;

    /// <summary>The bucket after the one the previous build ended at, or the first.</summary>
    private int ResumeIndex(List<Bucket> buckets)
    {
        if (_resumeAfter is not { } after) return 0;
        for (int i = 0; i < buckets.Count; i++)
            if (buckets[i].Transactions[0].Hash!.Bytes.SequenceCompareTo(after.Bytes) > 0) return i;
        return 0;
    }

    /// <remarks>Not called for a bucket the prover was too busy for, so the next cycle retries it rather than skipping it; other
    /// failures still advance, so a bucket that cannot be built does not stall the rotation.</remarks>
    private void Advance(List<Bucket> buckets, int built) =>
        _resumeAfter = built == buckets.Count - 1 ? null : buckets[built].Transactions[^1].Hash!.ValueHash256;

    /// <summary>Whether to skip proving <paramref name="deps"/> this time, as a peer offers a proof of exactly them.</summary>
    /// <remarks>Each dependency set waits at most <see cref="ReuseOfferWait"/>, so an unavailable or invalid offer does not prevent
    /// local aggregation (EIP-8288).</remarks>
    private bool AwaitsOffer(List<FrameDependency> deps, TimeProvider clock)
    {
        if (IsDependencySetOffered is not { } offered) return false;
        ValueHash256 digest = ValueKeccak.Compute(Eip8288Dependencies.Serialize(deps));
        if (!offered(digest)) return false;
        long now = clock.GetTimestamp();
        if (!_offerWaits.TryGetValue(digest, out long since)) _offerWaits[digest] = since = now;
        return clock.GetElapsedTime(since, now) < ReuseOfferWait;
    }

    private void PruneOfferWaits(TimeProvider clock)
    {
        if (_offerWaits.Count == 0) return;
        long now = clock.GetTimestamp();
        List<ValueHash256>? expired = null;
        foreach ((ValueHash256 digest, long since) in _offerWaits)
            if (clock.GetElapsedTime(since, now) >= OfferWaitMemory) (expired ??= []).Add(digest);
        foreach (ValueHash256 digest in expired ?? []) _offerWaits.Remove(digest);
    }

    /// <param name="unchanged">Whether the bucket is the latest wrapper's transactions again, which it returns as is.</param>
    private Result<byte[]> BuildBucket(Bucket bucket, Eip8288Verifier verifier, bool returnEncoded, TimeProvider clock,
        CancellationToken cancellationToken, out bool unchanged)
    {
        unchanged = false;
        List<Transaction> transactions = bucket.Transactions;
        List<FrameDependency> deps = bucket.Deps;
        AggregationInput selectedInput = bucket.Input;
        byte[] selection = new byte[transactions.Count * Hash256.Size];
        for (int i = 0; i < transactions.Count; i++) transactions[i].Hash!.Bytes.CopyTo(selection.AsSpan(i * Hash256.Size));
        ValueHash256 selectionHash = ValueKeccak.Compute(selection);
        Hash256[] hashes = new Hash256[transactions.Count];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = transactions[i].Hash!;
        WrapperSnapshot? cached = Volatile.Read(ref _cachedWrapper);
        if (cached?.Selection == selectionHash && cached.Verifier == verifier && ArePending(hashes))
        {
            unchanged = true;
            return Result<byte[]>.Success(returnEncoded ? (byte[])cached.Encoded.Clone() : []);
        }
        try
        {
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
            if (!leanProofStore.TryGetRecursiveProof(deps, out byte[]? proof))
            {
                if (AwaitsOffer(deps, clock)) return Result<byte[]>.Fail(AwaitingOfferedProof);
                if (cached is not null && cached.Verifier == verifier && HasOverlap(cached.Parent.InnerDeps, [.. deps]))
                {
                    AggregationInput incremental = RecursiveStarkAggregator.Combine(
                        [new() { RecursiveProofs = [cached.Parent] }, selectedInput], deps);
                    if (deps.Count + incremental.Discards.Count <= Eip8288Constants.MaxProofDependencies)
                        selectedInput = incremental;
                }
                proof = RecursiveStarkAggregator.Prove(selectedInput, leanProofVerifier, verifier, in hash, cancellationToken);
                if (leanProofVerifier is not ProductionProofCache
                    && !leanProofVerifier.VerifyRecursiveStark(in hash, verifier.AggregatedVk.Bytes, proof))
                    return Result<byte[]>.Fail("Produced dependency proof failed verification.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            MempoolWrapper wrapper = new()
            {
                Transactions = transactions,
                Deps = deps,
                Mode = MempoolWrapper.ModeRecursive,
                RecursiveStark = new RecursiveStark(proof!, new Hash256(hash))
            };
            lock (_commitLock)
            {
                if (RefreshVerifier() != verifier) return Result<byte[]>.Fail(VerifierChangedDuringAdmission);
                leanProofStore.AddCachedRecursive(deps, proof!);
            }
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes;
            if (encoded.Length > Eip8288Constants.MaxWrapperBytes)
                return Result<byte[]>.Fail("Aggregated wrapper exceeds the size limit.");
            if (!ArePending(hashes))
                return Result<byte[]>.Fail("Proof wrapper selection changed while aggregating; retry with the current pool.");
            RecursiveProofInput parent = new(deps, proof!);
            Volatile.Write(ref _cachedWrapper, new(selectionHash, hashes, (byte[])encoded.Clone(), ValueKeccak.Compute(encoded), parent, verifier));
            return Result<byte[]>.Success(returnEncoded ? encoded : []);
        }
        catch (InvalidOperationException exception)
        {
            return Result<byte[]>.Fail(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Result<byte[]>.Fail(exception.Message);
        }
    }

    /// <summary>Adds the blob pool's dependency-bearing frame transactions in the elided network form EIP-8437 carries:
    /// commitments and cell proofs without blob data, which peers retrieve as EIP-8070 cells.</summary>
    private void AddBlobCandidates(List<Transaction> candidates, in FeeFloors floors)
    {
        List<Transaction> executable = [];
        foreach ((AddressAsKey sender, Transaction[] senderTransactions) in txPool.GetPendingLightBlobTransactionsBySender())
            AddExecutable(sender, senderTransactions, floors, executable);
        foreach (Transaction light in executable)
            if (light.GetProofVersion() == ProofVersion.V1 && Eip8288Dependencies.IsDependencyBearing(light)
                && txPool.TryGetPendingTransactionWithoutBlobs(light.Hash!.ValueHash256, out Transaction? elided))
                candidates.Add(elided);
    }

    /// <summary>The estimated proving cost of <paramref name="input"/>, or of extending <paramref name="parent"/> with it when
    /// that is cheaper, as the build does when the previous wrapper's statement overlaps.</summary>
    private static long ProvingCost(AggregationInput input, IReadOnlyList<FrameDependency> required, RecursiveProofInput? parent)
    {
        long cost = RecursiveStarkAggregator.EstimatedCost(input);
        if (parent is not { } proven || cost == 0) return cost;
        AggregationInput extended = RecursiveStarkAggregator.Combine([new() { RecursiveProofs = [proven] }, input], required);
        return Math.Min(cost, RecursiveStarkAggregator.EstimatedCost(extended));
    }

    private static bool HasOverlap(IReadOnlyList<FrameDependency> dependencies, HashSet<FrameDependency> selected)
    {
        foreach (FrameDependency dependency in dependencies)
            if (selected.Contains(dependency)) return true;
        return false;
    }

    private bool ArePending(IReadOnlyList<Hash256> transactions)
    {
        foreach (Hash256 hash in transactions)
            if (!txPool.TryGetPendingTransaction(hash.ValueHash256, out _)) return false;
        return true;
    }

    /// <summary>Whether any of <paramref name="transactions"/> is still pending.</summary>
    internal bool IsAnyPending(ReadOnlySpan<ValueHash256> transactions)
    {
        foreach (ValueHash256 hash in transactions)
            if (txPool.TryGetPendingTransaction(hash, out _)) return true;
        return false;
    }

    /// <summary>Whether the target fork enables dependency-proof wrappers.</summary>
    public bool IsEnabled => TargetSpec?.IsEip8288Enabled == true;

    /// <summary>Whether any scheduled fork of this chain enables EIP-8288; when not, wrapper work never needs to run.</summary>
    public bool IsScheduled => specProvider.GetFinalSpec().IsEip8288Enabled;

    private bool Preflight(Transaction transaction, out string? error, IReleaseSpec? admissionSpec = null)
    {
        error = null;
        if (!transaction.SupportsFrames) return true;
        admissionSpec ??= TargetSpec;
        if (admissionSpec is null)
        {
            error = "EIP-8288 proof wrappers are unavailable.";
            return false;
        }
        IReleaseSpec spec = admissionSpec;
        if (!FrameTxValidation.IsWellFormed(transaction, spec, out error)) return false;
        ValidationResult nonceKeys = FrameTxNonceKeysTxValidator.Instance.IsWellFormed(transaction, spec);
        error = nonceKeys.Error;
        return nonceKeys;
    }

    private static MempoolWrapper DecodeProofWrapper(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        MempoolWrapper wrapper = MempoolWrapperDecoder.Instance.Decode(ref reader);
        reader.Check(encoded.Length);
        return wrapper;
    }
}
