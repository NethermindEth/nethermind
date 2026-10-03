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
    private readonly object _aggregationLock = new();
    private int _rotation;
    private int _admissionActive;
    private readonly Dictionary<ValueHash256, string?> _verifiedWrappers = [];
    private readonly Queue<ValueHash256> _verifiedWrapperOrder = [];
    private ValueHash256? _cachedSelection;
    private byte[]? _cachedWrapper;

    /// <summary>Verifies a wrapper and admits its proof-backed transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptAsync(byte[] wrapper, CancellationToken cancellationToken = default)
        => (await AcceptDetailedAsync(wrapper, cancellationToken)).Result;

    /// <summary>Separates invalid proofs from ordinary pool admission failures.</summary>
    public async Task<ProofWrapperAcceptance> AcceptDetailedAsync(byte[] wrapper, CancellationToken cancellationToken = default)
    {
        Block? head = blockFinder.Head;
        IReleaseSpec? admissionSpec = head is null ? null : specProvider.GetSpec(head.Header);
        if (admissionSpec?.IsEip8288Enabled != true)
            return ProofWrapperAcceptance.LocalFailure("EIP-8288 proof wrappers are unavailable.");
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes)
            return ProofWrapperAcceptance.Invalid("Proof wrapper exceeds the size limit.");
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
            Dictionary<ValueHash256, Transaction> resolved = [];
            foreach (WrapperTransaction entry in decoded.Transactions)
            {
                Transaction? transaction = entry.Full ?? (entry.Hash is null ? null : ResolvePending(entry.Hash));
                if (transaction is null) return ProofWrapperAcceptance.LocalFailure(MempoolWrapperValidator.UnknownTransaction);
                if (!Preflight(transaction, out string? frameError, admissionSpec))
                    return frameError is FrameTxValidation.PostTxNotEnabled or FrameTxValidation.KeyedNoncesNotEnabled
                        or FrameTxValidation.RecentRootReferencesNotEnabled or FrameTxValidation.LegacyNonceNotAllowed
                        ? ProofWrapperAcceptance.LocalFailure(frameError) : ProofWrapperAcceptance.Invalid(frameError!);
                if (transaction.RecentRootReferences is { Length: > 0 })
                {
                    if (headInfo is null) return ProofWrapperAcceptance.LocalFailure("Recent-root state is unavailable.");
                    if (!AreAdmissionRootsValid(transaction))
                        return ProofWrapperAcceptance.LocalFailure(TxPoolErrorMessages.FrameTxRecentRootUnmet);
                }
                if (entry.Hash is { } hash) resolved[hash.ValueHash256] = transaction;
            }
            ValueHash256 wrapperHash = ValueKeccak.Compute(wrapper);
            if (!_verifiedWrappers.TryGetValue(wrapperHash, out string? error))
            {
                (bool valid, string? validationError) = await Task.Run(() =>
                {
                    bool proofValid = MempoolWrapperValidator.Validate(decoded, leanProofVerifier, out string? proofError,
                        hash => resolved.GetValueOrDefault(hash.ValueHash256));
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

    /// <summary>Verifies a proof-bearing inclusion list and admits its transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptInclusionListAsync(byte[] inclusionList, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return Result<Hash256[]>.Fail("EIP-8288 proof inclusion lists are unavailable.");
        if (inclusionList.Length > LeanProofStore.MaxWrapperBytes)
            return Result<Hash256[]>.Fail("Proof inclusion list exceeds the size limit.");
        if (Interlocked.CompareExchange(ref _admissionActive, 1, 0) != 0)
            return Result<Hash256[]>.Fail("Proof admission is busy; retry later.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            inclusionList = (byte[])inclusionList.Clone();
            InclusionListProofPackage decoded = DecodeProofInclusionList(inclusionList);
            foreach (Transaction transaction in decoded.Transactions)
            {
                if (!Preflight(transaction, out string? frameError)) return Result<Hash256[]>.Fail(frameError!);
                if (transaction.RecentRootReferences is { Length: > 0 })
                {
                    if (headInfo is null) return Result<Hash256[]>.Fail("Recent-root state is unavailable.");
                    if (!AreAdmissionRootsValid(transaction)) return Result<Hash256[]>.Fail(TxPoolErrorMessages.FrameTxRecentRootUnmet);
                }
            }
            (bool valid, string? error) = await Task.Run(() =>
            {
                bool proofValid = InclusionListProofValidator.Validate(decoded, leanProofVerifier, out string? proofError);
                return (proofValid, proofError);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!valid) return Result<Hash256[]>.Fail(error!);
            cancellationToken.ThrowIfCancellationRequested();
            List<FrameDependency> canonical = Eip8288Dependencies.Parse(decoded.ProvenDependencies ?? []);
            List<WrapperTransaction> transactions = [];
            foreach (Transaction transaction in decoded.Transactions)
                transactions.Add(new WrapperTransaction(transaction));
            List<FrameDependency> admittedDependencies = [];
            Result<Hash256[]> admission;
            if (!leanProofStore.TryBeginAdmission(canonical, null, decoded.RecursiveStark.StarkProof, out IDisposable? scope))
                return Result<Hash256[]>.Fail("Proof witness capacity is full; retry later.");
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
            return admission;
        }
        catch (RlpException)
        {
            return Result<Hash256[]>.Fail("Invalid proof inclusion list RLP.");
        }
        catch (ArgumentException exception)
        {
            return Result<Hash256[]>.Fail(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Result<Hash256[]>.Fail(exception.Message);
        }
        finally { Volatile.Write(ref _admissionActive, 0); }
    }

    private void RememberVerification(ValueHash256 hash, string? error)
    {
        if (_verifiedWrapperOrder.Count == 128) _verifiedWrappers.Remove(_verifiedWrapperOrder.Dequeue());
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
            AcceptTxResult result = txPool.SubmitTx(entry.Full, TxHandlingOptions.PersistentBroadcast);
            if (result != AcceptTxResult.Accepted) firstError ??= result.ToString();
            else admittedDependencies.AddRange(Eip8288Dependencies.ForTransaction(entry.Full));
            hashes[i] = hash;
        }
        return Task.FromResult(firstError is null ? Result<Hash256[]>.Success(hashes) : Result<Hash256[]>.Fail(firstError));
    }

    /// <summary>Builds a recursive wrapper over the current proof-backed pool view.</summary>
    public Result<byte[]> BuildWrapper(bool skipEmpty = false, CancellationToken cancellationToken = default)
    {
        if (!Monitor.TryEnter(_aggregationLock)) return Result<byte[]>.Fail("Proof aggregation is busy; retry later.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BuildWrapperCore(skipEmpty, cancellationToken);
        }
        finally { Monitor.Exit(_aggregationLock); }
    }

    /// <summary>Returns the latest background-produced wrapper without invoking the prover.</summary>
    public Result<byte[]> GetLatestWrapper()
    {
        if (!IsEnabled) return Result<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.");
        byte[]? wrapper = Volatile.Read(ref _cachedWrapper);
        return wrapper is null ? Result<byte[]>.Fail("Proof wrapper is not ready; retry after the next background cycle.")
            : Result<byte[]>.Success((byte[])wrapper.Clone());
    }

    private Result<byte[]> BuildWrapperCore(bool skipEmpty, CancellationToken cancellationToken)
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
                if (sphincs > Eip8288Constants.MaxLeanSigDepsPerWrapper || stark > Eip8288Constants.MaxLeanStarkDepsPerWrapper)
                {
                    if (transactions.Count != 0) { next = candidateIndex; break; }
                    continue;
                }
                if (!leanProofStore.TryGetInput(combined, out AggregationInput candidateInput)
                    || combined.Count + candidateInput.Discards.Count > Eip8288Constants.MaxProofDependencies
                    || RecursiveStarkAggregator.InputSize(candidateInput) > RecursiveStarkAggregator.MaxProductionWitnessBytes) continue;
                deps = combined;
                selectedInput = candidateInput;
                covered.UnionWith(transactionDeps);
            }
            transactionBytes += encodedLength;
            transactions.Add(new WrapperTransaction(transaction));
            next = (candidateIndex + 1) % candidates.Count;
        }
        _rotation = next;
        if (skipEmpty && transactions.Count == 0)
            return Result<byte[]>.Fail("No proof-backed pending transactions.");
        transactions.Sort(static (a, b) => a.Full!.Hash!.Bytes.SequenceCompareTo(b.Full!.Hash!.Bytes));
        byte[] selection = new byte[transactions.Count * Hash256.Size];
        for (int i = 0; i < transactions.Count; i++) transactions[i].Full!.Hash!.Bytes.CopyTo(selection.AsSpan(i * Hash256.Size));
        ValueHash256 selectionHash = ValueKeccak.Compute(selection);
        if (_cachedSelection == selectionHash && _cachedWrapper is not null)
            return Result<byte[]>.Success((byte[])_cachedWrapper.Clone());
        try
        {
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
            if (!leanProofStore.TryGetRecursiveProof(deps, out byte[]? proof))
            {
                proof = RecursiveStarkAggregator.Prove(selectedInput, leanProofVerifier, in hash, cancellationToken);
                if (!leanProofVerifier.VerifyRecursiveStark(in hash, Eip8288Constants.AggregatedVk, proof))
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
            _cachedSelection = selectionHash;
            Volatile.Write(ref _cachedWrapper, (byte[])encoded.Clone());
            return Result<byte[]>.Success(encoded);
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
