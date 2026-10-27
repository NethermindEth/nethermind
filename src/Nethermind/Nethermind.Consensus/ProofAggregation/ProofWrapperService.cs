// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Validators;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Shared proof-wrapper validation, admission and aggregation for RPC and eth/73 peers.</summary>
/// <remarks>
/// Wrappers are validated under their target fork, which fixes the verifier: the fork active at the next slot's timestamp,
/// predicted from the local head with the local slot-length estimate (see <see cref="WrapperTargetFork"/>).
/// When the local head advances to a target fork whose verifier differs, the EIP-8288 upgrade rule applies: every held
/// aggregate proof is dropped, direct witnesses are kept only if they re-validate, and pending transactions left without
/// proof coverage leave the pool.
/// </remarks>
public sealed class ProofWrapperService(ITxPool txPool, ISpecProvider specProvider,
    IBlockFinder blockFinder, LeanProofStore leanProofStore, ILeanProofVerifier leanProofVerifier)
{
    /// <summary>Encoded transaction bytes a produced mode-1 wrapper may carry: <c>MAX_WRAPPER_BYTES</c> less the largest
    /// aggregate proof, a full dependency list and the list framing.</summary>
    internal const int MaxWrapperTransactionBytes = Eip8288Constants.MaxWrapperBytes - Eip8288Constants.MaxAggregateProofBytes
        - Eip8288Constants.MaxDepsPerWrapper * (2 + Eip8288Constants.DependencyTripleLength) - 64;

    private const string BlobsUnsupported = "Blob-carrying dependency transactions are not supported.";
    private const int MaxRememberedVerifications = 128;
    private readonly object _aggregationLock = new();
    private int _rotation;
    private int _admissionActive;
    // Remembered verifications are keyed by AGGREGATED_VK too, so none answers under another verifier.
    private readonly Dictionary<(ValueHash256 Wrapper, ValueHash256 Verifier), string?> _verifiedWrappers = [];
    private readonly Queue<(ValueHash256 Wrapper, ValueHash256 Verifier)> _verifiedWrapperOrder = [];
    private sealed record WrapperSnapshot(ValueHash256 Selection, Hash256[] Transactions, byte[] Encoded, ValueHash256 Hash, RecursiveProofInput Parent,
        Eip8288Verifier Verifier);
    private WrapperSnapshot? _cachedWrapper;
    private readonly Lock _verifierLock = new();
    private readonly Lock _commitLock = new();
    private const string VerifierChangedDuringAdmission = "The target fork's verifier changed during validation; retry.";
    private Eip8288Verifier? _verifier;
    private TargetForks? _targetForks;

    /// <summary>The target fork and the fork after it, predicted from <paramref name="Head"/>.</summary>
    private sealed record TargetForks(Hash256? Head, IReleaseSpec Target, IReleaseSpec Following);

    /// <summary>Verifies a wrapper and admits its proof-backed transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => (await AcceptDetailedAsync(wrapper, cancellationToken)).Result;

    /// <summary>Raised with the encoded wrapper once the pool admits every transaction it carries and the verifier it was
    /// validated under is still current.</summary>
    /// <remarks>The second argument is the verifier it was validated under. Subscribers must not mutate the wrapper.</remarks>
    public event Action<byte[], Eip8288Verifier>? WrapperValidated;

    /// <summary>Raised with the new verifier after the upgrade rule dropped what the previous one validated.</summary>
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

    /// <summary>The verifier the upgrade rule last applied, read without refreshing it.</summary>
    public Eip8288Verifier? CurrentVerifier
    {
        get { lock (_verifierLock) return _verifier; }
    }

    /// <summary>
    /// The verifier of the current target fork, after applying the EIP-8288 upgrade rule if it changed; null while EIP-8288 is
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
            // A half-applied upgrade rule must run again on the next refresh rather than be skipped as already applied.
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

    /// <summary>EIP-8288 upgrade rule: drops every aggregate proof, keeps direct witnesses only if they re-validate under
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
            return (dropped, evicted);
        }
    }

    /// <summary>Separates malformed wrappers and invalid proofs from ordinary pool admission failures.</summary>
    public Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => AcceptDetailedCoreAsync(wrapper, true, cancellationToken);

    /// <summary>Accepts an exclusively owned transport buffer without another encoded-body copy.</summary>
    /// <remarks>The transport must retain its charged buffer throughout the returned task and must not mutate it.
    /// Validation subscribers must treat the encoded body as read-only.</remarks>
    internal Task<ProofWrapperAcceptance> AcceptOwnedDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => AcceptDetailedCoreAsync(wrapper, false, cancellationToken);

    private async Task<ProofWrapperAcceptance> AcceptDetailedCoreAsync(byte[] wrapper, bool copyInput, CancellationToken cancellationToken)
    {
        IReleaseSpec? admissionSpec = TargetSpec;
        if (admissionSpec?.IsEip8288Enabled != true)
            return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
        if (wrapper.Length > Eip8288Constants.MaxWrapperBytes)
            return ProofWrapperAcceptance.Malformed("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        if (Interlocked.CompareExchange(ref _admissionActive, 1, 0) != 0)
            return new(ProofWrapperAcceptanceStatus.Busy, Result<Hash256[]>.Fail("Proof admission is busy; retry later."));
        try
        {
            // Inside the try, so an upgrade-rule fault is a local failure rather than an exception escaping to the caller.
            if (RefreshVerifier() is not { } verifier) return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
            cancellationToken.ThrowIfCancellationRequested();
            if (copyInput) wrapper = (byte[])wrapper.Clone();
            MempoolWrapper decoded;
            try { decoded = DecodeProofWrapper(wrapper); }
            catch (Exception exception) when (exception is RlpException or ArgumentException)
            {
                return ProofWrapperAcceptance.Malformed($"Invalid proof wrapper RLP: {exception.Message}");
            }
            foreach (Transaction transaction in decoded.Transactions)
            {
                // EIP-8437 carries blob-carrying frame transactions with their blob data elided, for EIP-8070 cell retrieval,
                // which this client does not implement for dependency-bearing transactions.
                if (transaction.CarriesBlobs) return ProofWrapperAcceptance.Refused(BlobsUnsupported);
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
            // Nothing validated under a verifier the upgrade rule has since replaced may be published or admitted.
            if (RefreshVerifier() != verifier) return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
            // Coverage keeps one record per dependency, so a verified recursive proof can otherwise be lost to a direct
            // witness or eviction; indexed by its exact set, it stays reusable by producers restricted to proven sets. The
            // upgrade rule purges under the commit lock, so a proof cached here never outlives the verifier it was checked under.
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
            lock (_commitLock)
            {
                if (RefreshVerifier() != verifier) return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
                if (!leanProofStore.TryBeginAdmission(decoded.Deps, decoded.Proofs, decoded.RecursiveStark?.StarkProof, out scope))
                    return ProofWrapperAcceptance.LocalFailure("Proof witness capacity is full; retry later.");
            }
            using (scope)
            {
                try
                {
                    admission = SubmitProofTransactions(decoded.Transactions, cancellationToken, admittedDependencies);
                }
                finally
                {
                    leanProofStore.CommitAdmission(admittedDependencies);
                }
            }
            // A verifier change during pool insertion purged before this admission committed; purge its leftovers.
            if (RefreshVerifier() is { } current && current != verifier) Purge(current);
            // Relay only once every transaction it carries is pending, so a partly rejected wrapper is not relayed.
            if (admission.IsSuccess && RefreshVerifier() == verifier)
                WrapperValidated?.Invoke(wrapper, verifier);
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
        finally { Volatile.Write(ref _admissionActive, 0); }
    }

    private void RememberVerification((ValueHash256, ValueHash256) hash, string? error)
    {
        if (_verifiedWrappers.ContainsKey(hash)) return;
        if (_verifiedWrapperOrder.Count == MaxRememberedVerifications) _verifiedWrappers.Remove(_verifiedWrapperOrder.Dequeue());
        _verifiedWrapperOrder.Enqueue(hash);
        _verifiedWrappers[hash] = error;
    }

    private Result<Hash256[]> SubmitProofTransactions(IReadOnlyList<Transaction> transactions, CancellationToken cancellationToken, List<FrameDependency> admittedDependencies)
    {
        Hash256[] hashes = new Hash256[transactions.Count];
        string? firstError = null;
        for (int i = 0; i < hashes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transaction transaction = transactions[i];
            Hash256 hash = transaction.Hash!;
            hashes[i] = hash;
            if (txPool.TryGetPendingTransaction(hash.ValueHash256, out _))
            {
                admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue));
                continue;
            }
            // Admission is per transaction: a pool rejection, including EIP-8272 recent-root mempool rules, leaves the proof and
            // the other transactions intact.
            AcceptTxResult result = txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            if (result != AcceptTxResult.Accepted) firstError ??= result.ToString();
            else admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue));
        }
        return firstError is null ? Result<Hash256[]>.Success(hashes) : Result<Hash256[]>.Fail(firstError);
    }

    /// <summary>Builds a recursive wrapper over the current proof-backed pool view.</summary>
    /// <remarks>Fails when no proof-backed transaction is pending, since a wrapper carries at least one.</remarks>
    public Result<byte[]> BuildWrapper(CancellationToken cancellationToken = default)
        => BuildWrapper(true, cancellationToken);

    /// <summary>One <c>AGGREGATION_INTERVAL</c> cycle of background aggregation: builds bounded wrappers, continuing the
    /// rotation, until every pending dependency-bearing transaction has been offered to one, calling
    /// <paramref name="produced"/> after each, so each can be read through <see cref="GetLatestWrapper()"/> and published.</summary>
    /// <remarks>Stops at the first build failure; the next cycle resumes the rotation where this one stopped.</remarks>
    /// <returns>Success when at least one wrapper is current; otherwise the first failure.</returns>
    public Result RefreshWrapper(Action? produced = null, CancellationToken cancellationToken = default)
    {
        if (!Monitor.TryEnter(_aggregationLock)) return Result.Fail("Proof aggregation is busy; retry later.");
        try
        {
            using IDisposable request = ProductionProofCache.Background(cancellationToken);
            bool any = false;
            for (int scanned = 0; ;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Result<byte[]> result = BuildWrapperCore(false, cancellationToken, out int buildScanned, out int candidates);
                scanned += buildScanned;
                // A stretch of the rotation without proof-backed transactions ends no cycle that has more candidates.
                if (result.IsSuccess) { any = true; produced?.Invoke(); }
                else if (result.Error != NoProofBackedTransactions) return any ? Result.Success : Result.Fail(result.Error!);
                if (buildScanned == 0 || scanned >= candidates)
                    return any ? Result.Success : Result.Fail(result.Error ?? NoProofBackedTransactions);
            }
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
            return BuildWrapperCore(returnEncoded, cancellationToken, out _, out _);
        }
        catch (InvalidOperationException exception)
        {
            return Result<byte[]>.Fail(exception.Message);
        }
        finally { Monitor.Exit(_aggregationLock); }
    }

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
        // A pending upgrade rule leaves the cached wrapper unpublishable until it applies.
        try { verifier = RefreshVerifier(); }
        catch (InvalidOperationException) { verifier = null; }
        if (wrapper is null || wrapper.Verifier != verifier || !ArePending(wrapper.Transactions))
            return Result<byte[]>.Fail("Proof wrapper is not ready; retry after the next background cycle.");
        hash = wrapper.Hash;
        return Result<byte[]>.Success(knownHash == hash ? [] : (byte[])wrapper.Encoded.Clone());
    }

    /// <param name="scanned">Candidates this build passed over, from the rotation point to where the wrapper filled.</param>
    /// <param name="candidateCount">Pending transactions considered.</param>
    private Result<byte[]> BuildWrapperCore(bool returnEncoded, CancellationToken cancellationToken, out int scanned, out int candidateCount)
    {
        scanned = 0;
        candidateCount = 0;
        if (RefreshVerifier() is not { } verifier)
            return Result<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.");
        List<Transaction> transactions = [];
        List<FrameDependency> deps = [];
        HashSet<FrameDependency> covered = [];
        AggregationInput selectedInput = new();
        int transactionBytes = 0;
        // Blob-carrying transactions are left out: their wrapped form elides blob data for EIP-8070 cell retrieval.
        List<Transaction> candidates = [.. txPool.GetPendingTransactions()];
        candidates.Sort(static (a, b) => a.Hash!.Bytes.SequenceCompareTo(b.Hash!.Bytes));
        candidateCount = candidates.Count;
        int start = candidates.Count == 0 ? 0 : (int)((uint)_rotation % (uint)candidates.Count);
        int next = 0;
        int end = candidates.Count;
        for (int candidateIndex = start; candidateIndex < candidates.Count; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transaction transaction = candidates[candidateIndex];
            if (!transaction.SupportsFrames || transaction.CarriesBlobs) continue;
            List<FrameDependency> transactionDeps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
            if (transactionDeps.Count == 0) continue;
            int encodedLength = Rlp.LengthOfSequence(TxDecoder.Instance.GetLength(transaction, MempoolWrapperDecoder.TransactionBehaviors));
            if (encodedLength > MaxWrapperTransactionBytes) continue;
            if (encodedLength > MaxWrapperTransactionBytes - transactionBytes)
            {
                if (transactions.Count != 0) { next = end = candidateIndex; break; }
                continue;
            }
            bool addsDependencies = false;
            foreach (FrameDependency dependency in transactionDeps)
                if (!covered.Contains(dependency)) { addsDependencies = true; break; }
            if (addsDependencies)
            {
                List<FrameDependency> combined = [.. deps, .. transactionDeps];
                combined = Eip8288Dependencies.Canonicalize(combined);
                (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(combined);
                if (sphincs > Eip8288Constants.MaxLeanSigDepsPerWrapper || stark > Eip8288Constants.MaxLeanStarkDepsPerWrapper)
                {
                    if (transactions.Count != 0) { next = end = candidateIndex; break; }
                    continue;
                }
                if (!leanProofStore.TryGetInput(combined, out AggregationInput candidateInput)
                    || combined.Count + candidateInput.Discards.Count > Eip8288Constants.MaxProofDependencies) continue;
                deps = combined;
                selectedInput = candidateInput;
                covered.UnionWith(transactionDeps);
            }
            transactionBytes += encodedLength;
            transactions.Add(transaction);
            next = (candidateIndex + 1) % candidates.Count;
        }
        // A build that reached the end of the candidates starts the next one from the beginning.
        _rotation = end == candidates.Count ? 0 : next;
        scanned = end - start;
        if (transactions.Count == 0)
            return Result<byte[]>.Fail(NoProofBackedTransactions);
        transactions.Sort(static (a, b) => a.Hash!.Bytes.SequenceCompareTo(b.Hash!.Bytes));
        byte[] selection = new byte[transactions.Count * Hash256.Size];
        for (int i = 0; i < transactions.Count; i++) transactions[i].Hash!.Bytes.CopyTo(selection.AsSpan(i * Hash256.Size));
        ValueHash256 selectionHash = ValueKeccak.Compute(selection);
        Hash256[] hashes = new Hash256[transactions.Count];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = transactions[i].Hash!;
        WrapperSnapshot? cached = Volatile.Read(ref _cachedWrapper);
        if (cached?.Selection == selectionHash && cached.Verifier == verifier && ArePending(hashes))
            return Result<byte[]>.Success(returnEncoded ? (byte[])cached.Encoded.Clone() : []);
        try
        {
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
            if (!leanProofStore.TryGetRecursiveProof(deps, out byte[]? proof))
            {
                if (cached is not null && cached.Verifier == verifier && HasOverlap(cached.Parent.InnerDeps, covered))
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
