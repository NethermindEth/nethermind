// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Validators;
using Nethermind.Serialization.Rlp;
using Nethermind.Evm;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Shared proof-wrapper validation, admission and aggregation for RPC and negotiated peers.</summary>
public sealed class ProofWrapperService(ITxPool txPool, ISpecProvider specProvider,
    IBlockFinder blockFinder, LeanProofStore leanProofStore, ILeanProofVerifier leanProofVerifier, IChainHeadInfoProvider? headInfo = null)
{
    internal const int MaxWrapperTransactionBytes = LeanProofStore.MaxWrapperBytes - Eip8288Constants.MaxProofBytes - 4096;

    /// <summary>Local scheduling target for leanSPHINCS dependencies in one produced mode-1 batch.</summary>
    /// <remarks>Not a validity limit: received aggregates may carry up to <see cref="Eip8288Constants.MaxDepsPerAggregate"/>.</remarks>
    public const int MaxBatchSigDependencies = 16;

    /// <summary>Local scheduling target for leanSTARK dependencies in one produced mode-1 batch.</summary>
    public const int MaxBatchStarkDependencies = 1;

    private const string LocalAggregateCapacity = "Aggregate exceeds the local proof capacity.";
    private const int MaxRememberedVerifications = 128;
    private readonly object _aggregationLock = new();
    private int _rotation;
    private int _admissionActive;
    private readonly Dictionary<ValueHash256, string?> _verifiedWrappers = [];
    private readonly Queue<ValueHash256> _verifiedWrapperOrder = [];
    private readonly HashSet<ValueHash256> _verifiedClaims = [];
    private readonly Queue<ValueHash256> _verifiedClaimOrder = [];
    private sealed record WrapperSnapshot(ValueHash256 Selection, Hash256[] Transactions, byte[] Encoded, ValueHash256 Hash, RecursiveProofInput Parent);
    private WrapperSnapshot? _cachedWrapper;

    /// <summary>Verifies a wrapper and admits its proof-backed transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => (await AcceptDetailedAsync(wrapper, cancellationToken)).Result;

    /// <summary>Raised with the encoded wrapper once it is fully validated, whether or not the pool admitted its transactions.</summary>
    /// <remarks>The second argument holds the transactions that resolved the wrapper's hash entries, so a relay can keep
    /// serving the envelopes it offers by hash.</remarks>
    public event Action<byte[], IReadOnlyList<Transaction>>? WrapperValidated;

    /// <summary>Raised with the encoded inclusion-list package once its proof validated.</summary>
    public event Action<byte[]>? InclusionListValidated;

    /// <summary>Separates invalid proofs from ordinary pool admission failures.</summary>
    public Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => AcceptDetailedAsync(wrapper, null, cancellationToken);

    /// <summary>Validates and admits a wrapper whose hash entries may resolve to transactions recovered from peers.</summary>
    /// <param name="wrapper">The encoded wrapper.</param>
    /// <param name="recovered">Envelopes for hash entries not pending locally, already checked against their hashes.</param>
    /// <param name="cancellationToken">Cancels validation and admission.</param>
    public async Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction>? recovered,
        CancellationToken cancellationToken = default)
    {
        Block? head = blockFinder.Head;
        IReleaseSpec? admissionSpec = head is null ? null : specProvider.GetSpec(head.Header);
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
            cancellationToken.ThrowIfCancellationRequested();
            wrapper = (byte[])wrapper.Clone();
            MempoolWrapper decoded;
            try { decoded = DecodeProofWrapper(wrapper); }
            catch (Exception exception) when (exception is RlpException or ArgumentException)
            {
                return ProofWrapperAcceptance.Invalid("Invalid proof wrapper RLP.");
            }
            if (ExceedsLocalCapacity(decoded)) return ProofWrapperAcceptance.LocalFailure(LocalAggregateCapacity);
            IReadOnlyList<WrapperTransaction> received = decoded.Transactions;
            if (recovered is { Count: > 0 }) decoded = WithRecovered(decoded, recovered);
            // Envelopes resolving the received hash entries, from the pool or recovered from peers.
            Dictionary<ValueHash256, Transaction> resolved = [];
            for (int i = 0; i < decoded.Transactions.Count; i++)
            {
                WrapperTransaction entry = decoded.Transactions[i];
                Transaction? transaction = entry.Full ?? (entry.Hash is null ? null : ResolvePending(entry.Hash));
                if (transaction is null) return ProofWrapperAcceptance.LocalFailure(MempoolWrapperValidator.UnknownTransaction);
                if (!Preflight(transaction, out string? frameError, admissionSpec))
                    return frameError is FrameTxValidation.PostTxNotEnabled or FrameTxValidation.KeyedNoncesNotEnabled
                        or FrameTxValidation.RecentRootReferencesNotEnabled or FrameTxValidation.LegacyNonceNotAllowed
                        ? ProofWrapperAcceptance.LocalFailure(frameError) : ProofWrapperAcceptance.Invalid(frameError!);
                if (received[i].Hash is { } hash) resolved[hash.ValueHash256] = transaction;
            }
            ValueHash256 wrapperHash = ValueKeccak.Compute(wrapper);
            if (!_verifiedWrappers.TryGetValue(wrapperHash, out string? error))
            {
                bool claimsVerified = _verifiedClaims.Contains(wrapperHash);
                (bool valid, string? validationError) = await Task.Run(() =>
                {
                    bool proofValid = MempoolWrapperValidator.Validate(decoded, leanProofVerifier, out string? proofError,
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
            WrapperValidated?.Invoke(wrapper, [.. resolved.Values]);
            cancellationToken.ThrowIfCancellationRequested();
            List<FrameDependency> admittedDependencies = [];
            Result<Hash256[]> admission;
            if (!leanProofStore.TryBeginAdmission(decoded.Deps, decoded.Proofs, decoded.RecursiveStark?.StarkProof, out IDisposable? scope))
                return ProofWrapperAcceptance.LocalFailure("Proof witness capacity is full; retry later.");
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
    /// The EIP-8437 preliminary check of a wrapper whose hash entries are still missing: verifies its witnesses or aggregate
    /// proof against the claimed dependencies, so an invalid proof is rejected before any transaction is fetched.
    /// </summary>
    /// <remarks>
    /// A pass does not validate the wrapper, since the proof does not authenticate the transaction list; it only lets
    /// <see cref="AcceptDetailedAsync(byte[], IReadOnlyDictionary{ValueHash256, Transaction}?, CancellationToken)"/> skip
    /// verifying the same proofs again once the entries are resolved. The result is <see cref="ProofWrapperAcceptanceStatus.Accepted"/>
    /// when the claimed proofs verify.
    /// </remarks>
    public async Task<ProofWrapperAcceptance> VerifyClaimedProofsAsync(byte[] wrapper, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes) return ProofWrapperAcceptance.LocalFailure("Proof wrapper exceeds the local size limit.");
        if (Interlocked.CompareExchange(ref _admissionActive, 1, 0) != 0)
            return new(ProofWrapperAcceptanceStatus.Busy, Result<Hash256[]>.Fail("Proof admission is busy; retry later."));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            wrapper = (byte[])wrapper.Clone();
            MempoolWrapper decoded;
            try { decoded = DecodeProofWrapper(wrapper); }
            catch (Exception exception) when (exception is RlpException or ArgumentException)
            {
                return ProofWrapperAcceptance.Invalid("Invalid proof wrapper RLP.");
            }
            if (ExceedsLocalCapacity(decoded)) return ProofWrapperAcceptance.LocalFailure(LocalAggregateCapacity);
            ValueHash256 wrapperHash = ValueKeccak.Compute(wrapper);
            if (_verifiedWrappers.TryGetValue(wrapperHash, out string? known))
                return known is null ? new(ProofWrapperAcceptanceStatus.Accepted, Result<Hash256[]>.Success([])) : ProofWrapperAcceptance.Invalid(known);
            if (!_verifiedClaims.Contains(wrapperHash))
            {
                (bool valid, string? error) = await Task.Run(() =>
                {
                    bool proofValid = MempoolWrapperValidator.VerifyClaimedProofs(decoded, leanProofVerifier, out string? proofError);
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
        finally { Volatile.Write(ref _admissionActive, 0); }
    }

    /// <summary>Whether an aggregate exceeds what the local proof store and prover can hold, a non-penalizing refusal.</summary>
    private static bool ExceedsLocalCapacity(MempoolWrapper wrapper)
        => wrapper.Mode == MempoolWrapper.ModeRecursive && wrapper.Deps.Count > Eip8288Constants.MaxProofDependencies;

    /// <summary>Verifies a proof-bearing inclusion list and admits its transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptInclusionListAsync(byte[] inclusionList, CancellationToken cancellationToken = default)
        => (await AcceptInclusionListDetailedAsync(inclusionList, cancellationToken)).Result;

    /// <summary>Separates an invalid inclusion-list package from local and pool admission failures.</summary>
    /// <remarks>An empty dependency set carries an empty proof and <c>get_deps_hash([])</c>, accepted without invoking the
    /// verifier, as for a block.</remarks>
    public async Task<ProofWrapperAcceptance> AcceptInclusionListDetailedAsync(byte[] inclusionList, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof inclusion lists are unavailable.");
        if (inclusionList.Length > LeanProofStore.MaxWrapperBytes)
            return ProofWrapperAcceptance.LocalFailure("Proof inclusion list exceeds the local size limit.");
        if (Interlocked.CompareExchange(ref _admissionActive, 1, 0) != 0)
            return new(ProofWrapperAcceptanceStatus.Busy, Result<Hash256[]>.Fail("Proof admission is busy; retry later."));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            inclusionList = (byte[])inclusionList.Clone();
            InclusionListProofPackage decoded;
            try { decoded = DecodeProofInclusionList(inclusionList); }
            catch (RlpException) { return ProofWrapperAcceptance.Invalid("Invalid proof inclusion list RLP."); }
            foreach (Transaction transaction in decoded.Transactions)
                if (!Preflight(transaction, out string? frameError)) return ProofWrapperAcceptance.Invalid(frameError!);
            (bool valid, List<FrameDependency> canonical, string? error) = await Task.Run(() =>
            {
                bool proofValid = InclusionListProofValidator.Validate(decoded, leanProofVerifier, out List<FrameDependency> proven, out string? proofError);
                return (proofValid, proven, proofError);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!valid) return ProofWrapperAcceptance.Invalid(error!);
            InclusionListValidated?.Invoke(inclusionList);
            List<WrapperTransaction> transactions = [];
            foreach (Transaction transaction in decoded.Transactions)
                transactions.Add(new WrapperTransaction(transaction));
            List<FrameDependency> admittedDependencies = [];
            Result<Hash256[]> admission;
            if (!leanProofStore.TryBeginAdmission(canonical, null, decoded.RecursiveStark.StarkProof, out IDisposable? scope))
                return ProofWrapperAcceptance.LocalFailure("Proof witness capacity is full; retry later.");
            using (scope)
            {
                try
                {
                    admission = await SubmitProofTransactions(transactions, cancellationToken, admittedDependencies);
                }
                finally
                {
                    leanProofStore.CommitAdmission(admittedDependencies);
                }
            }
            return new(admission.IsSuccess ? ProofWrapperAcceptanceStatus.Accepted : ProofWrapperAcceptanceStatus.PoolRejected, admission);
        }
        catch (RlpException)
        {
            return ProofWrapperAcceptance.Invalid("Invalid proof inclusion list RLP.");
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

    private void RememberVerification(ValueHash256 hash, string? error)
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
                if (ResolvePending(entry.Hash!) is { } pending) admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(pending));
                else firstError ??= MempoolWrapperValidator.UnknownTransaction;
                continue;
            }
            if (entry.Full.Hash is { } existingHash && txPool.TryGetPendingTransaction(existingHash.ValueHash256, out _))
            {
                hashes[i] = existingHash;
                admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(entry.Full));
                continue;
            }
            Hash256 hash = entry.Full.Hash!;
            hashes[i] = hash;
            // Admission is per transaction: a current-state rejection leaves the proof and the other transactions intact.
            if (entry.Full.RecentRootReferences is { Length: > 0 })
            {
                if (headInfo is null)
                {
                    firstError ??= "Recent-root state is unavailable.";
                    continue;
                }
                if (!AreAdmissionRootsValid(entry.Full))
                {
                    firstError ??= TxPoolErrorMessages.FrameTxRecentRootUnmet;
                    continue;
                }
            }
            AcceptTxResult result = txPool.SubmitTx(entry.Full, TxHandlingOptions.PersistentBroadcast);
            if (result != AcceptTxResult.Accepted) firstError ??= result.ToString();
            else admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(entry.Full));
        }
        return Task.FromResult(firstError is null ? Result<Hash256[]>.Success(hashes) : Result<Hash256[]>.Fail(firstError));
    }

    /// <summary>Builds a recursive wrapper over the current proof-backed pool view.</summary>
    /// <remarks>Fails when no proof-backed transaction is pending, since an EIP-8437 kind-1 body carries at least one.</remarks>
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
        if (wrapper is null || !ArePending(wrapper.Transactions))
            return Result<byte[]>.Fail("Proof wrapper is not ready; retry after the next background cycle.");
        hash = wrapper.Hash;
        return Result<byte[]>.Success(knownHash == hash ? [] : (byte[])wrapper.Encoded.Clone());
    }

    private Result<byte[]> BuildWrapperCore(bool returnEncoded, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
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
            List<FrameDependency> transactionDeps = Eip8288Dependencies.ForTransaction(transaction);
            if (transactionDeps.Count == 0) continue;
            int encodedLength = Rlp.LengthOfByteString(TxDecoder.Instance.GetLength(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping), 0);
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
        if (cached?.Selection == selectionHash && ArePending(hashes))
            return Result<byte[]>.Success(returnEncoded ? (byte[])cached.Encoded.Clone() : []);
        try
        {
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
            if (!leanProofStore.TryGetRecursiveProof(deps, out byte[]? proof))
            {
                if (cached is not null && HasOverlap(cached.Parent.InnerDeps, covered))
                {
                    AggregationInput incremental = RecursiveStarkAggregator.Combine(
                        [new() { RecursiveProofs = [cached.Parent] }, selectedInput], deps);
                    if (deps.Count + incremental.Discards.Count <= Eip8288Constants.MaxProofDependencies)
                        selectedInput = incremental;
                }
                proof = RecursiveStarkAggregator.Prove(selectedInput, leanProofVerifier, in hash, cancellationToken);
                if (leanProofVerifier is not ProductionProofCache
                    && !leanProofVerifier.VerifyRecursiveStark(in hash, Eip8288Constants.AggregatedVk, proof))
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
            leanProofStore.AddCachedRecursive(deps, proof!);
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes;
            if (encoded.Length > LeanProofStore.MaxWrapperBytes)
                return Result<byte[]>.Fail("Aggregated wrapper exceeds the size limit.");
            if (!ArePending(hashes))
                return Result<byte[]>.Fail("Proof wrapper selection changed while aggregating; retry with the current pool.");
            RecursiveProofInput parent = new(deps, proof!);
            Volatile.Write(ref _cachedWrapper, new(selectionHash, hashes, (byte[])encoded.Clone(), ValueKeccak.Compute(encoded), parent));
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

    /// <summary>Whether the current head enables dependency-proof wrappers.</summary>
    public bool IsEnabled => blockFinder.Head is { } head && specProvider.GetSpec(head.Header).IsEip8288Enabled;

    private Transaction? ResolvePending(Hash256 hash) => txPool.TryGetPendingTransaction(hash.ValueHash256, out Transaction? transaction) ? transaction : null;

    private bool AreAdmissionRootsValid(Transaction transaction)
        => RecentRootReferences.Validate(headInfo!.ReadOnlyStateProvider, transaction.RecentRootReferences,
            headInfo.HeadSlotNumber is { } slot && slot < ulong.MaxValue ? slot + 1 : null);

    private bool Preflight(Transaction transaction, out string? error, IReleaseSpec? admissionSpec = null)
    {
        error = null;
        if (!transaction.SupportsFrames) return true;
        admissionSpec ??= blockFinder.Head is { } head ? specProvider.GetSpec(head.Header) : null;
        if (admissionSpec is null)
        {
            error = "EIP-8288 proof wrappers are unavailable.";
            return false;
        }
        IReleaseSpec spec = admissionSpec;
        if (!FrameTxValidation.IsWellFormed(transaction, spec, out error)) return false;
        ValidationResult nonceKeys = FrameTxNonceKeysTxValidator.Instance.IsWellFormed(transaction, spec);
        if (!nonceKeys)
        {
            error = nonceKeys.Error;
            return false;
        }
        ValidationResult envelope = FrameTxEnvelopeTxValidator.Instance.IsWellFormed(transaction, spec);
        error = envelope.Error;
        return envelope;
    }

    private static InclusionListProofPackage DecodeProofInclusionList(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        InclusionListProofPackage inclusionList = InclusionListProofPackageDecoder.Instance.Decode(ref reader);
        reader.Check(encoded.Length);
        return inclusionList;
    }

    private static MempoolWrapper DecodeProofWrapper(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        MempoolWrapper wrapper = MempoolWrapperDecoder.Instance.Decode(ref reader);
        reader.Check(encoded.Length);
        return wrapper;
    }
}
