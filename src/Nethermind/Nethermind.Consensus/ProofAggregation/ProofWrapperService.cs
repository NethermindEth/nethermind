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
    internal const int MaxWrapperTransactionBytes = Eip8288Constants.MaxTxsPerWrapper * (Hash256.Size + 1);

    /// <summary>Local scheduling target for leanSPHINCS dependencies in one produced mode-1 batch.</summary>
    /// <remarks>Not a validity limit: received aggregates may carry up to <see cref="Eip8288Constants.MaxDepsPerAggregateWrapper"/>.</remarks>
    private const int MaxBatchSigDependencies = 16;

    /// <summary>Local scheduling target for leanSTARK dependencies in one produced mode-1 batch.</summary>
    private const int MaxBatchStarkDependencies = 1;

    private const string LocalAggregateCapacity = "Aggregate exceeds the local proof capacity.";
    private const int MaxRememberedVerifications = 128;
    private readonly object _aggregationLock = new();
    private int _rotation;
    private int _admissionActive;
    // Remembered verifications are keyed by AGGREGATED_VK too, so none answers under another verifier.
    private readonly Dictionary<(ValueHash256 Wrapper, ValueHash256 Verifier), string?> _verifiedWrappers = [];
    private readonly Queue<(ValueHash256 Wrapper, ValueHash256 Verifier)> _verifiedWrapperOrder = [];
    private readonly HashSet<(ValueHash256 Wrapper, ValueHash256 Verifier)> _verifiedClaims = [];
    private readonly Queue<(ValueHash256 Wrapper, ValueHash256 Verifier)> _verifiedClaimOrder = [];
    private sealed record WrapperSnapshot(ValueHash256 Selection, Hash256[] Transactions, byte[] Encoded, ValueHash256 Hash, RecursiveProofInput Parent,
        Eip8288Verifier Verifier);
    private WrapperSnapshot? _cachedWrapper;
    private readonly Lock _verifierLock = new();
    private readonly Lock _commitLock = new();
    private const string VerifierChangedDuringAdmission = "The target fork's verifier changed during validation; retry.";
    private Eip8288Verifier? _verifier;
    private long _verifierChangedAt = -1;
    private TargetForks? _targetForks;

    /// <summary>The target fork and the fork after it, predicted from <paramref name="Head"/>.</summary>
    private sealed record TargetForks(Hash256? Head, IReleaseSpec Target, IReleaseSpec Following);

    /// <summary>Verifies a wrapper and admits its proof-backed transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => (await AcceptDetailedAsync(wrapper, cancellationToken)).Result;

    /// <summary>Raised with the encoded wrapper once the pool admits every transaction it carries, at least one carries a
    /// dependency, and the verifier it was validated under is still current.</summary>
    /// <remarks>The second argument maps each of the wrapper's hash entries to the transaction that resolved it, so a relay
    /// can keep serving the envelopes it offers by hash, and the third is the verifier it was validated under. Subscribers must
    /// not mutate the arguments.</remarks>
    public event Action<byte[], IReadOnlyDictionary<ValueHash256, Transaction>, Eip8288Verifier>? WrapperValidated;

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
            _verifierChangedAt = (long)head.Number;
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

    /// <summary>
    /// Whether proof failures from peers should be local refusals: from when the local target fork switched to a fork that
    /// changed the verifier until the head is <paramref name="windowBlocks"/> blocks past that fork, or when the fork after the
    /// target fork changes it.
    /// </summary>
    /// <remarks>EIP-8437 leaves this window to local policy, since peers negotiate no verifier and their target-fork estimates
    /// can differ by a slot around an activation or a slot-length change; the second condition covers peers whose estimate
    /// switches a slot before this node's.</remarks>
    public bool IsInVerifierTransition(long windowBlocks)
    {
        if (blockFinder.Head is not { } head) return false;
        long changedAt = Volatile.Read(ref _verifierChangedAt);
        // The fork's first block is the one after the head at which the change was seen.
        if (changedAt >= 0 && (long)head.Number < changedAt + 1 + windowBlocks) return true;
        (_, IReleaseSpec target, IReleaseSpec following) = TargetForksOf(head.Header);
        return target.IsEip8288Enabled && following.IsEip8288Enabled && target.Eip8288AggregatedVk != following.Eip8288AggregatedVk;
    }

    /// <summary>Whether a held wrapper re-validates under <paramref name="verifier"/>: a mode-0 wrapper whose direct witnesses
    /// verify under its schemes' parameters. A mode-1 proof never verifies under another <c>AGGREGATED_VK</c>, so it never does.</summary>
    public bool Revalidates(byte[] wrapper, in Eip8288Verifier verifier)
    {
        MempoolWrapper decoded;
        try { decoded = DecodeProofWrapper(wrapper); }
        catch (Exception exception) when (exception is RlpException or ArgumentException) { return false; }
        return decoded.Mode == MempoolWrapper.ModeDirect && MempoolWrapperValidator.VerifyClaimedProofs(decoded, leanProofVerifier, in verifier, out _);
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

    /// <summary>Separates invalid proofs from ordinary pool admission failures.</summary>
    public Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => AcceptDetailedAsync(wrapper, null, cancellationToken);

    /// <summary>Validates and admits a wrapper whose hash entries may resolve to transactions recovered from peers.</summary>
    /// <param name="wrapper">The encoded wrapper.</param>
    /// <param name="recovered">Envelopes for hash entries not pending locally, already checked against their hashes.</param>
    /// <param name="cancellationToken">Cancels validation and admission.</param>
    public Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction>? recovered,
        CancellationToken cancellationToken = default)
        => AcceptDetailedCoreAsync(wrapper, recovered, true, cancellationToken);

    /// <summary>Accepts an exclusively owned transport buffer without another encoded-body copy.</summary>
    /// <remarks>The transport must retain its charged buffer throughout the returned task and must not mutate it.
    /// Decoding still owns a separate proof copy; validation subscribers must treat the encoded body as read-only.</remarks>
    internal Task<ProofWrapperAcceptance> AcceptOwnedDetailedAsync(byte[] wrapper,
        IReadOnlyDictionary<ValueHash256, Transaction>? recovered, CancellationToken cancellationToken = default)
        => AcceptDetailedCoreAsync(wrapper, recovered, false, cancellationToken);

    private async Task<ProofWrapperAcceptance> AcceptDetailedCoreAsync(byte[] wrapper,
        IReadOnlyDictionary<ValueHash256, Transaction>? recovered, bool copyInput, CancellationToken cancellationToken)
    {
        IReleaseSpec? admissionSpec = TargetSpec;
        if (admissionSpec?.IsEip8288Enabled != true)
            return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
        // A wrapper within MAX_WRAPPER_BYTES but above the local limit is refused, not invalid.
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes)
            return wrapper.Length > Eip8288Constants.MaxWrapperBytes
                ? ProofWrapperAcceptance.Invalid("Proof wrapper exceeds MAX_WRAPPER_BYTES.")
                : ProofWrapperAcceptance.LocalFailure("Proof wrapper exceeds the local size limit.");
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
                return ProofWrapperAcceptance.Invalid("Invalid proof wrapper RLP.");
            }
            if (MempoolWrapperValidator.ExceedsLocalVerifier(decoded)) return ProofWrapperAcceptance.Refused(LocalAggregateCapacity);
            IReadOnlyList<WrapperTransaction> received = decoded.Transactions;
            if (recovered is { Count: > 0 })
            {
                HashSet<ValueHash256> claimed = [];
                foreach (WrapperTransaction entry in received) claimed.Add(entry.Hash!.ValueHash256);
                foreach ((ValueHash256 hash, Transaction transaction) in recovered)
                {
                    Hash256 canonicalHash = transaction.CalculateHash();
                    if (!claimed.Contains(hash) || canonicalHash.ValueHash256 != hash)
                        return ProofWrapperAcceptance.Invalid("Recovered envelope does not match a proof wrapper transaction hash.");
                    transaction.Hash = canonicalHash;
                }
                decoded = WithRecovered(decoded, recovered);
            }
            // Envelopes resolving the received hash entries, from the pool or recovered from peers.
            Dictionary<ValueHash256, Transaction> resolved = [];
            for (int i = 0; i < decoded.Transactions.Count; i++)
            {
                WrapperTransaction entry = decoded.Transactions[i];
                Transaction? transaction = entry.Full ?? (entry.Hash is null ? null : ResolvePending(entry.Hash));
                if (transaction is null) return ProofWrapperAcceptance.LocalFailure(MempoolWrapperValidator.UnknownTransaction);
                if (!Preflight(transaction, out string? frameError, admissionSpec))
                    return frameError is FrameTxValidation.AssertFrameNotEnabled or FrameTxValidation.KeyedNoncesNotEnabled
                        or FrameTxValidation.LegacyNonceNotAllowed
                        ? ProofWrapperAcceptance.LocalFailure(frameError) : ProofWrapperAcceptance.Invalid(frameError!);
                if (received[i].Hash is { } hash) resolved[hash.ValueHash256] = transaction;
            }
            (ValueHash256, ValueHash256) wrapperHash = (ValueKeccak.Compute(wrapper), verifier.AggregatedVk);
            if (!_verifiedWrappers.TryGetValue(wrapperHash, out string? error))
            {
                bool claimsVerified = _verifiedClaims.Contains(wrapperHash);
                (bool valid, string? validationError) = await Task.Run(() =>
                {
                    bool proofValid = MempoolWrapperValidator.Validate(decoded, leanProofVerifier, in verifier, out string? proofError,
                        hash => resolved.GetValueOrDefault(hash.ValueHash256), claimsVerified);
                    return (proofValid, proofError);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                error = validationError;
                if (!valid)
                {
                    if (error == MempoolWrapperValidator.UnknownTransaction) return ProofWrapperAcceptance.LocalFailure(error);
                    RememberVerification(wrapperHash, error);
                    return ProofWrapperAcceptance.Invalid(error!);
                }
                RememberVerification(wrapperHash, null);
            }
            else if (error is not null) return ProofWrapperAcceptance.Invalid(error);
            // Nothing validated under a verifier the upgrade rule has since replaced may be published or admitted.
            if (RefreshVerifier() != verifier) return ProofWrapperAcceptance.LocalFailure(VerifierChangedDuringAdmission);
            // Coverage keeps one record per dependency, so a verified recursive proof can otherwise be lost to a direct
            // witness or eviction; indexed by its exact set, it stays reusable by producers restricted to proven sets. The
            // upgrade rule purges under the commit lock, so a proof cached here never outlives the verifier it was checked under.
            if (decoded.RecursiveStark is { } recursive && decoded.Deps.Count != 0)
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
                    admission = await SubmitProofTransactions(decoded.Transactions, cancellationToken, admittedDependencies);
                }
                finally
                {
                    leanProofStore.CommitAdmission(admittedDependencies);
                }
            }
            // A verifier change during pool insertion purged before this admission committed; purge its leftovers.
            if (RefreshVerifier() is { } current && current != verifier) Purge(current);
            // EIPs#12473: relay only once usable for admission, i.e. every transaction it carries is pending and at least one
            // carries a dependency. Already-pending entries also fill admittedDependencies, so a partly rejected wrapper must not relay.
            if (admission.IsSuccess && admittedDependencies.Count != 0 && RefreshVerifier() == verifier)
                WrapperValidated?.Invoke(wrapper, resolved, verifier);
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

    /// <summary>
    /// A preliminary check of a wrapper whose envelopes are still missing: verifies its witnesses or aggregate proof against
    /// the claimed dependencies, so an invalid proof is rejected before any envelope is recovered.
    /// </summary>
    /// <remarks>
    /// A pass does not validate the wrapper, since the proof does not authenticate the transaction list; it only lets
    /// <see cref="AcceptDetailedAsync(byte[], IReadOnlyDictionary{ValueHash256, Transaction}?, CancellationToken)"/> skip
    /// verifying the same proofs again once the entries are resolved. The result is <see cref="ProofWrapperAcceptanceStatus.Accepted"/>
    /// when the claimed proofs verify.
    /// </remarks>
    public Task<ProofWrapperAcceptance> VerifyClaimedProofsAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => VerifyClaimedProofsCoreAsync(wrapper, true, cancellationToken);

    /// <summary>Prechecks claimed proofs from an exclusively owned, charged transport buffer.</summary>
    /// <remarks>The caller must retain the buffer without mutation until the task completes. The decoder owns its proof copy.</remarks>
    internal Task<ProofWrapperAcceptance> VerifyClaimedProofsOwnedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => VerifyClaimedProofsCoreAsync(wrapper, false, cancellationToken);

    private async Task<ProofWrapperAcceptance> VerifyClaimedProofsCoreAsync(byte[] wrapper, bool copyInput, CancellationToken cancellationToken)
    {
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes) return ProofWrapperAcceptance.LocalFailure("Proof wrapper exceeds the local size limit.");
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
                return ProofWrapperAcceptance.Invalid("Invalid proof wrapper RLP.");
            }
            if (MempoolWrapperValidator.ExceedsLocalVerifier(decoded)) return ProofWrapperAcceptance.Refused(LocalAggregateCapacity);
            (ValueHash256, ValueHash256) wrapperHash = (ValueKeccak.Compute(wrapper), verifier.AggregatedVk);
            if (_verifiedWrappers.TryGetValue(wrapperHash, out string? known))
                return known is null ? new(ProofWrapperAcceptanceStatus.Accepted, Result<Hash256[]>.Success([])) : ProofWrapperAcceptance.Invalid(known);
            if (!_verifiedClaims.Contains(wrapperHash))
            {
                (bool valid, string? error) = await Task.Run(() =>
                {
                    bool proofValid = MempoolWrapperValidator.VerifyClaimedProofs(decoded, leanProofVerifier, in verifier, out string? proofError);
                    return (proofValid, proofError);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!valid)
                {
                    RememberVerification(wrapperHash, error);
                    return ProofWrapperAcceptance.Invalid(error!);
                }
                if (_verifiedClaimOrder.Count == MaxRememberedVerifications) _verifiedClaims.Remove(_verifiedClaimOrder.Dequeue());
                _verifiedClaimOrder.Enqueue(wrapperHash);
                _verifiedClaims.Add(wrapperHash);
            }
            return new(ProofWrapperAcceptanceStatus.Accepted, Result<Hash256[]>.Success([]));
        }
        catch (InvalidOperationException exception)
        {
            return ProofWrapperAcceptance.LocalFailure(exception.Message);
        }
        finally { Volatile.Write(ref _admissionActive, 0); }
    }

    private MempoolWrapper WithRecovered(MempoolWrapper wrapper, IReadOnlyDictionary<ValueHash256, Transaction> recovered)
    {
        List<WrapperTransaction> transactions = new(wrapper.Transactions.Count);
        foreach (WrapperTransaction entry in wrapper.Transactions)
            transactions.Add(entry.Hash is { } hash && ResolvePending(hash) is null && recovered.TryGetValue(hash.ValueHash256, out Transaction? transaction)
                ? new WrapperTransaction(transaction)
                : entry);
        return new MempoolWrapper
        {
            Transactions = transactions,
            Mode = wrapper.Mode,
            Deps = wrapper.Deps,
            Proofs = wrapper.Proofs,
            RecursiveStark = wrapper.RecursiveStark
        };
    }

    private void RememberVerification((ValueHash256, ValueHash256) hash, string? error)
    {
        if (_verifiedWrappers.ContainsKey(hash)) return;
        if (_verifiedWrapperOrder.Count == MaxRememberedVerifications) _verifiedWrappers.Remove(_verifiedWrapperOrder.Dequeue());
        _verifiedWrapperOrder.Enqueue(hash);
        _verifiedWrappers[hash] = error;
    }

    private Task<Result<Hash256[]>> SubmitProofTransactions(IReadOnlyList<WrapperTransaction> transactions, CancellationToken cancellationToken, List<FrameDependency> admittedDependencies)
    {
        Hash256[] hashes = new Hash256[transactions.Count];
        string? firstError = null;
        for (int i = 0; i < hashes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrapperTransaction entry = transactions[i];
            if (entry.Full is null)
            {
                hashes[i] = entry.Hash!;
                if (ResolvePending(entry.Hash!) is { } pending) admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(pending, FrameTxSigHash.ComputeValue));
                else firstError ??= MempoolWrapperValidator.UnknownTransaction;
                continue;
            }
            if (entry.Full.Hash is { } existingHash && txPool.TryGetPendingTransaction(existingHash.ValueHash256, out _))
            {
                hashes[i] = existingHash;
                admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(entry.Full, FrameTxSigHash.ComputeValue));
                continue;
            }
            Hash256 hash = entry.Full.Hash!;
            hashes[i] = hash;
            // Admission is per transaction: a pool rejection, including EIP-8272 recent-root mempool rules, leaves the proof and
            // the other transactions intact.
            AcceptTxResult result = txPool.SubmitTx(entry.Full, TxHandlingOptions.PersistentBroadcast);
            if (result != AcceptTxResult.Accepted) firstError ??= result.ToString();
            else admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(entry.Full, FrameTxSigHash.ComputeValue));
        }
        return Task.FromResult(firstError is null ? Result<Hash256[]>.Success(hashes) : Result<Hash256[]>.Fail(firstError));
    }

    /// <summary>Builds a recursive wrapper over the current proof-backed pool view.</summary>
    /// <remarks>Fails when no proof-backed transaction is pending, since a wrapper carries at least one.</remarks>
    public Result<byte[]> BuildWrapper(CancellationToken cancellationToken = default)
        => BuildWrapper(true, cancellationToken);

    /// <summary>Advances background aggregation without copying the encoded result for an unused return value.</summary>
    public Result RefreshWrapper(CancellationToken cancellationToken = default)
    {
        Result<byte[]> result = BuildWrapper(false, cancellationToken);
        return result.IsSuccess ? Result.Success : Result.Fail(result.Error!);
    }

    private Result<byte[]> BuildWrapper(bool returnEncoded, CancellationToken cancellationToken)
    {
        if (!Monitor.TryEnter(_aggregationLock)) return Result<byte[]>.Fail("Proof aggregation is busy; retry later.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IDisposable request = ProductionProofCache.Background(cancellationToken);
            return BuildWrapperCore(returnEncoded, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return Result<byte[]>.Fail(exception.Message);
        }
        finally { Monitor.Exit(_aggregationLock); }
    }

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

    private Result<byte[]> BuildWrapperCore(bool returnEncoded, CancellationToken cancellationToken)
    {
        if (RefreshVerifier() is not { } verifier)
            return Result<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.");
        List<WrapperTransaction> transactions = [];
        List<FrameDependency> deps = [];
        HashSet<FrameDependency> covered = [];
        AggregationInput selectedInput = new();
        int transactionBytes = 0;
        List<Transaction> candidates = [.. txPool.GetPendingTransactions()];
        foreach (Transaction[] blobBucket in txPool.GetPendingLightBlobTransactionsBySender().Values)
            foreach (Transaction light in blobBucket)
                if (light.Hash is { } hash && txPool.TryGetPendingTransaction(hash.ValueHash256, out Transaction? full)) candidates.Add(full);
        candidates.Sort(static (a, b) => a.Hash!.Bytes.SequenceCompareTo(b.Hash!.Bytes));
        int start = candidates.Count == 0 ? 0 : (int)((uint)_rotation % (uint)candidates.Count);
        int next = 0;
        for (int candidateIndex = start; candidateIndex < candidates.Count; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transactions.Count == LeanProofStore.MaxWrapperTransactions) break;
            Transaction transaction = candidates[candidateIndex];
            if (!transaction.SupportsFrames) continue;
            List<FrameDependency> transactionDeps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
            if (transactionDeps.Count == 0) continue;
            int encodedLength = Rlp.LengthOf(transaction.Hash);
            if (encodedLength > MaxWrapperTransactionBytes) continue;
            if (encodedLength > MaxWrapperTransactionBytes - transactionBytes)
            {
                if (transactions.Count != 0) { next = candidateIndex; break; }
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
                if (sphincs > MaxBatchSigDependencies || stark > MaxBatchStarkDependencies)
                {
                    if (transactions.Count != 0) { next = candidateIndex; break; }
                    continue;
                }
                if (!leanProofStore.TryGetInput(combined, out AggregationInput candidateInput)
                    || combined.Count + candidateInput.Discards.Count > Eip8288Constants.MaxProofDependencies) continue;
                deps = combined;
                selectedInput = candidateInput;
                covered.UnionWith(transactionDeps);
            }
            transactionBytes += encodedLength;
            transactions.Add(new WrapperTransaction(transaction));
            next = (candidateIndex + 1) % candidates.Count;
        }
        _rotation = next;
        if (transactions.Count == 0)
            return Result<byte[]>.Fail("No proof-backed pending transactions.");
        transactions.Sort(static (a, b) => a.Full!.Hash!.Bytes.SequenceCompareTo(b.Full!.Hash!.Bytes));
        byte[] selection = new byte[transactions.Count * Hash256.Size];
        for (int i = 0; i < transactions.Count; i++) transactions[i].Full!.Hash!.Bytes.CopyTo(selection.AsSpan(i * Hash256.Size));
        ValueHash256 selectionHash = ValueKeccak.Compute(selection);
        Hash256[] hashes = new Hash256[transactions.Count];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = transactions[i].Full!.Hash!;
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
            if (encoded.Length > LeanProofStore.MaxWrapperBytes)
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

    private Transaction? ResolvePending(Hash256 hash) => txPool.TryGetPendingTransaction(hash.ValueHash256, out Transaction? transaction) ? transaction : null;

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
