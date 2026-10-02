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
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Eip8288;

/// <summary>Shared proof-wrapper validation, admission and aggregation for RPC and negotiated peers.</summary>
public sealed class ProofWrapperService(ITxPool txPool, ITxSender txSender, ISpecProvider specProvider,
    IBlockFinder blockFinder, LeanProofStore leanProofStore, ILeanProofVerifier leanProofVerifier)
{
    private readonly object _aggregationLock = new();
    private int _rotation;
    private ValueHash256? _cachedSelection;
    private byte[]? _cachedWrapper;

    /// <summary>Verifies a wrapper and admits its proof-backed transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptAsync(byte[] wrapper, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return Result<Hash256[]>.Fail("EIP-8288 proof wrappers are unavailable.");
        if (wrapper.Length > LeanProofStore.MaxWrapperBytes)
            return Result<Hash256[]>.Fail("Proof wrapper exceeds the size limit.");
        try
        {
            MempoolWrapper decoded = DecodeProofWrapper(wrapper);
            if (!MempoolWrapperValidator.Validate(decoded, leanProofVerifier, out string? error, ResolvePending))
                return Result<Hash256[]>.Fail(error!);
            leanProofStore.AddVerified(decoded.Deps, decoded.Proofs, decoded.RecursiveStark?.StarkProof);
            return await SubmitProofTransactions(decoded.Transactions, cancellationToken);
        }
        catch (RlpException)
        {
            return Result<Hash256[]>.Fail("Invalid proof wrapper RLP.");
        }
        catch (ArgumentException exception)
        {
            return Result<Hash256[]>.Fail(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Result<Hash256[]>.Fail(exception.Message);
        }
    }

    /// <summary>Verifies a proof-bearing inclusion list and admits its transactions.</summary>
    public async Task<Result<Hash256[]>> AcceptInclusionListAsync(byte[] inclusionList, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return Result<Hash256[]>.Fail("EIP-8288 proof inclusion lists are unavailable.");
        if (inclusionList.Length > LeanProofStore.MaxWrapperBytes)
            return Result<Hash256[]>.Fail("Proof inclusion list exceeds the size limit.");
        try
        {
            FocilInclusionList decoded = DecodeProofInclusionList(inclusionList);
            if (!FocilInclusionListValidator.Validate(decoded, leanProofVerifier, out string? error))
                return Result<Hash256[]>.Fail(error!);
            List<FrameDependency> deps = [];
            List<WrapperTransaction> transactions = [];
            foreach (Transaction transaction in decoded.Transactions)
            {
                deps.AddRange(Eip8288Dependencies.ForTransaction(transaction));
                transactions.Add(new WrapperTransaction(transaction));
            }
            leanProofStore.AddVerified(Eip8288Dependencies.Canonicalize(deps), null, decoded.RecursiveStark.StarkProof);
            return await SubmitProofTransactions(transactions, cancellationToken);
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
    }

    private async Task<Result<Hash256[]>> SubmitProofTransactions(IReadOnlyList<WrapperTransaction> transactions, CancellationToken cancellationToken)
    {
        Hash256[] hashes = new Hash256[transactions.Count];
        for (int i = 0; i < hashes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrapperTransaction entry = transactions[i];
            if (entry.Full is null)
            {
                hashes[i] = entry.Hash!;
                continue;
            }
            if (entry.Full.Hash is { } existingHash && txPool.TryGetPendingTransaction(existingHash.ValueHash256, out _))
            {
                hashes[i] = existingHash;
                continue;
            }
            (Hash256 hash, AcceptTxResult? result) = await txSender.SendTransaction(entry.Full, TxHandlingOptions.PersistentBroadcast);
            if (result != AcceptTxResult.Accepted)
                return Result<Hash256[]>.Fail(result?.ToString() ?? "Transaction submission failed.");
            hashes[i] = hash;
        }
        return Result<Hash256[]>.Success(hashes);
    }

    /// <summary>Builds a recursive wrapper over the current proof-backed pool view.</summary>
    public Result<byte[]> BuildWrapper(bool skipEmpty = false)
    {
        lock (_aggregationLock) return BuildWrapperCore(skipEmpty);
    }

    private Result<byte[]> BuildWrapperCore(bool skipEmpty)
    {
        if (!IsEnabled)
            return Result<byte[]>.Fail("EIP-8288 proof wrappers are unavailable.");
        List<WrapperTransaction> transactions = [];
        List<FrameDependency> deps = [];
        List<Transaction> candidates = [.. txPool.GetPendingTransactions()];
        foreach (Transaction[] blobBucket in txPool.GetPendingLightBlobTransactionsBySender().Values)
            foreach (Transaction light in blobBucket)
                if (light.Hash is { } hash && txPool.TryGetPendingTransaction(hash.ValueHash256, out Transaction? full)) candidates.Add(full);
        candidates.Sort(static (a, b) => a.Hash!.Bytes.SequenceCompareTo(b.Hash!.Bytes));
        int start = candidates.Count == 0 ? 0 : (int)((uint)_rotation++ % (uint)candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            Transaction transaction = candidates[(start + i) % candidates.Count];
            if (!transaction.SupportsFrames || Eip8288Dependencies.RecursiveStarkGas(transaction) == 0 || !leanProofStore.Covers(transaction)) continue;
            List<FrameDependency> combined = Eip8288Dependencies.Canonicalize(deps);
            combined.AddRange(Eip8288Dependencies.ForTransaction(transaction));
            combined = Eip8288Dependencies.Canonicalize(combined);
            (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(combined);
            if (sphincs > Eip8288Constants.MaxLeanSigDepsPerWrapper || stark > Eip8288Constants.MaxLeanStarkDepsPerWrapper) continue;
            if (!leanProofStore.TryGetInput(combined, out AggregationInput candidateInput)
                || RecursiveStarkAggregator.InputSize(candidateInput) > RecursiveStarkAggregator.MaxProductionWitnessBytes) continue;
            deps = combined;
            transactions.Add(new WrapperTransaction(transaction));
        }
        if (skipEmpty && transactions.Count == 0)
            return Result<byte[]>.Fail("No proof-backed pending transactions.");
        transactions.Sort(static (a, b) => a.Full!.Hash!.Bytes.SequenceCompareTo(b.Full!.Hash!.Bytes));
        byte[] selection = new byte[transactions.Count * Hash256.Size];
        for (int i = 0; i < transactions.Count; i++) transactions[i].Full!.Hash!.Bytes.CopyTo(selection.AsSpan(i * Hash256.Size));
        ValueHash256 selectionHash = ValueKeccak.Compute(selection);
        if (_cachedSelection == selectionHash && _cachedWrapper is not null)
            return Result<byte[]>.Success((byte[])_cachedWrapper.Clone());
        if (!leanProofStore.TryGetInput(deps, out AggregationInput input))
            return Result<byte[]>.Fail("Dependency witnesses expired; retry aggregation.");
        try
        {
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
            byte[] proof = RecursiveStarkAggregator.Prove(input, leanProofVerifier, in hash);
            if (!leanProofVerifier.VerifyRecursiveStark(in hash, Eip8288Constants.AggregatedVk, proof))
                return Result<byte[]>.Fail("Produced dependency proof failed verification.");
            MempoolWrapper wrapper = new()
            {
                Transactions = transactions,
                Deps = deps,
                Mode = MempoolWrapper.ModeRecursive,
                RecursiveStark = new RecursiveStark(proof, new Hash256(hash))
            };
            leanProofStore.AddVerified(deps, null, proof);
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes;
            if (encoded.Length > LeanProofStore.MaxWrapperBytes)
                return Result<byte[]>.Fail("Aggregated wrapper exceeds the size limit.");
            _cachedSelection = selectionHash;
            _cachedWrapper = (byte[])encoded.Clone();
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

    private static FocilInclusionList DecodeProofInclusionList(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        FocilInclusionList inclusionList = FocilInclusionListDecoder.Instance.Decode(ref reader);
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
