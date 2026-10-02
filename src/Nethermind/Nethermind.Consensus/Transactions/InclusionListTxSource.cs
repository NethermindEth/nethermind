// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core.Crypto;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(
    IEthereumEcdsa ecdsa,
    ISpecProvider specProvider,
    ILogManager logManager,
    ILeanProofVerifier proofVerifier, LeanProofStore? proofStore = null) : IInclusionListTxSource
{
    // Lazy<T> defaults to ExecutionAndPublication: constructed once even under racing FCUs.
    private readonly Lazy<InclusionListDecoder> _decoder = new(() => new InclusionListDecoder(ecdsa, specProvider, logManager));
    private readonly ILogger _logger = logManager.GetClassLogger<InclusionListTxSource>();

    // Keyed by the build's PayloadAttributes array so a concurrent FCU can't leak another build's IL;
    // weak keys collect with the build.
    private readonly ConditionalWeakTable<byte[][], BuildInclusionList> _decodedByAttributes = [];

    // gasLimit is ignored: the downstream tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, BlockHeader targetBlock, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
    {
        if (payloadAttributes?.InclusionListTransactions is not { Length: > 0 } il) return [];
        if (!_decodedByAttributes.TryGetValue(il, out BuildInclusionList? decoded))
        {
            // A miss means Set was never called for these attributes, e.g. an oversized IL that
            // engine_forkchoiceUpdatedV5 already warned about; debug-level as this runs once per improvement.
            if (_logger.IsDebug) _logger.Debug($"No inclusion list for this build ({il.Length} entries) — building without it.");
            return [];
        }

        if (decoded.DependenciesEnabled != specProvider.GetSpec(targetBlock).IsEip8288Enabled
            || !decoded.MatchesProof(payloadAttributes.InclusionListRecursiveStark)) return [];
        return (Transaction[])decoded.Transactions.Value.Clone();
    }

    /// <inheritdoc/>
    /// <remarks>Inputs are snapshotted within the IL byte bounds. Decoding, sender recovery and proof
    /// verification are deferred until the first <see cref="GetTransactions"/> call.</remarks>
    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec, RecursiveStark? proof = null)
    {
        if (inclusionListTransactions.Length > Eip7805Constants.MaxAggregateInclusionListTransactions) return;
        long bytes = 0;
        foreach (byte[] transaction in inclusionListTransactions)
        {
            bytes += transaction?.Length ?? 0;
            if (bytes > Eip7805Constants.MaxAggregateInclusionListBytes) return;
        }
        if (proof is not null && (proof.BlockDepsHash is null || proof.StarkProof is not { Length: > 0 and <= Eip8288Constants.MaxProofBytes })) return;
        if (_decodedByAttributes.TryGetValue(inclusionListTransactions, out BuildInclusionList? existing)
            && ReferenceEquals(existing.Spec, spec)
            && existing.MatchesInput(inclusionListTransactions, proof)) return;
        byte[][] snapshot = new byte[inclusionListTransactions.Length][];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = inclusionListTransactions[i] is { } entry ? entry.AsSpan().ToArray() : null!;
        RecursiveStark? proofSnapshot = proof is null ? null : new(proof.StarkProof.AsSpan().ToArray(), proof.BlockDepsHash);
        _decodedByAttributes.AddOrUpdate(inclusionListTransactions, new BuildInclusionList(spec, snapshot,
            proofSnapshot, new Lazy<Transaction[]>(() => PrepareSafely(snapshot, proofSnapshot, spec))));
    }

    private Transaction[] PrepareSafely(byte[][] snapshot, RecursiveStark? proof, IReleaseSpec spec)
    {
        try
        {
            return Prepare(snapshot, proof, spec);
        }
        catch (Exception ex) when (ex is RlpException or ArgumentException)
        {
            if (_logger.IsWarn) _logger.Warn($"Discarding malformed inclusion list ({ex.GetType().Name}: {ex.Message}).");
            return [];
        }
    }

    private Transaction[] Prepare(byte[][] snapshot, RecursiveStark? proof, IReleaseSpec spec)
    {
        Transaction[] transactions = _decoder.Value.DecodeAndRecover(snapshot, spec);
        if (spec.IsEip8288Enabled)
        {
            if (!FocilInclusionListValidator.Validate(transactions, proof, proofVerifier,
                out List<FrameDependency> deps, out string? error, spec))
            {
                if (_logger.IsWarn) _logger.Warn($"Discarding inclusion list: {error}.");
                return [];
            }
            if (deps.Count > 0)
            {
                if (proofStore is null) throw new InvalidOperationException("Inclusion-list dependencies require a proof store.");
                proofStore.AddVerified(deps, null, proof!.StarkProof);
            }
        }
        return OrderForProduction(FilterBlobs(transactions));
    }

    private sealed record BuildInclusionList(IReleaseSpec Spec, byte[][] EncodedTransactions, RecursiveStark? Proof, Lazy<Transaction[]> Transactions)
    {
        public bool DependenciesEnabled => Spec.IsEip8288Enabled;

        public bool MatchesInput(byte[][] transactions, RecursiveStark? proof)
        {
            if (transactions.Length != EncodedTransactions.Length || !MatchesProof(proof)) return false;
            for (int i = 0; i < transactions.Length; i++)
                if (!transactions[i].AsSpan().SequenceEqual(EncodedTransactions[i])) return false;
            return true;
        }

        public bool MatchesProof(RecursiveStark? proof) => Proof is null ? proof is null
            : proof is not null && Proof.BlockDepsHash == proof.BlockDepsHash && Proof.StarkProof.AsSpan().SequenceEqual(proof.StarkProof);
    }

    // The producer offers each IL tx once, so a shuffled IL would skip a nonce that arrives after its
    // dependent. Ordering by first-appearance rather than address avoids favouring low-address senders.
    private static Transaction[] OrderForProduction(Transaction[] txs)
    {
        if (txs.Length < 2) return txs;

        // Unrecoverable senders can never be included; group them together under Zero.
        Dictionary<AddressAsKey, int> firstSeen = new(txs.Length);
        int next = 0;
        foreach (Transaction tx in txs)
            if (firstSeen.TryAdd(tx.SenderAddress ?? Address.Zero, next)) next++;

        Array.Sort(txs, (a, b) =>
        {
            int bySender = firstSeen[a.SenderAddress ?? Address.Zero].CompareTo(firstSeen[b.SenderAddress ?? Address.Zero]);
            return bySender != 0 ? bySender : a.Nonce.CompareTo(b.Nonce);
        });
        return txs;
    }

    // Blob IL entries carry no ShardBlobNetworkWrapper, so including one would make getPayloadV6
    // unusable for the consensus client.
    private static Transaction[] FilterBlobs(Transaction[] txs)
    {
        int kept = 0;
        for (int i = 0; i < txs.Length; i++)
            if (!IsBlobCarrying(txs[i])) kept++;
        if (kept == txs.Length) return txs;

        Transaction[] result = new Transaction[kept];
        int j = 0;
        for (int i = 0; i < txs.Length; i++)
            if (!IsBlobCarrying(txs[i])) result[j++] = txs[i];
        return result;
    }

    /// <summary>Whether <paramref name="tx"/> would need a blob sidecar the inclusion list cannot carry.</summary>
    /// <remarks>
    /// An inclusion list is decoded from the canonical form, where the blob hashes are all that marks an
    /// EIP-8141 blob-carrying frame transaction, so the type-3-only <see cref="Transaction.SupportsBlobs"/>
    /// does not cover it on its own.
    /// </remarks>
    private static bool IsBlobCarrying(Transaction tx) => tx.SupportsBlobs || tx.CarriesBlobs;

    public bool SupportsBlobs => false;
}
