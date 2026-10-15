// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(
    IEthereumEcdsa ecdsa,
    ISpecProvider specProvider,
    ILogManager logManager,
    IProfile2EligibilityReplayer? profile2Replayer = null) : IInclusionListTxSource
{
    // Lazy<T> defaults to ExecutionAndPublication: constructed once even under racing FCUs.
    private readonly Lazy<InclusionListDecoder> _decoder = new(() => new InclusionListDecoder(ecdsa, specProvider, logManager));
    private readonly ILogger _logger = logManager.GetClassLogger<InclusionListTxSource>();

    // Keyed by the build's PayloadAttributes array so a concurrent FCU can't leak another build's lists;
    // weak keys collect with the build.
    private readonly ConditionalWeakTable<byte[][][], Lazy<DecodedInclusionLists>> _decodedByAttributes = [];

    /// <summary>A build's lists decoded twice over: as one deduplicated sequence ordered for production, and
    /// position by position for the per-IL VERIFY budget fill.</summary>
    private sealed record DecodedInclusionLists(Transaction[] ForProduction, Transaction[][] ByPosition, IReleaseSpec Spec)
    {
        /// <summary>The fill under the cap it ran with; every improvement of one build asks again.</summary>
        public (ulong MaxVerifyGasPerTx, IReadOnlySet<Hash256AsKey>? Candidates)? Profile2Candidates;
    }

    // gasLimit is ignored: the downstream tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, BlockHeader targetBlock, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => TryGetDecoded(payloadAttributes, out DecodedInclusionLists? decoded) ? decoded.ForProduction : [];

    public IReadOnlySet<Hash256AsKey>? GetProfile2Candidates(PayloadAttributes? payloadAttributes, ulong maxVerifyGasPerTx)
    {
        if (!TryGetDecoded(payloadAttributes, out DecodedInclusionLists? decoded)) return null;
        if (decoded.Profile2Candidates is { } cached && cached.MaxVerifyGasPerTx == maxVerifyGasPerTx) return cached.Candidates;

        IReleaseSpec spec = decoded.Spec;
        // EIP-7805: before Profile 2 is active no entry is a candidate, so the build claims nothing.
        if (!Eip8369Profile2.IsActive(spec)) return null;
        Func<Transaction, bool> signaturesValid = profile2Replayer is not null
            ? tx => profile2Replayer.AreSignaturesValid(tx, spec)
            : tx => Profile2EligibilityReplayer.AreSignaturesValid(tx, ecdsa, spec);
        HashSet<Hash256AsKey> admitted = Eip8369Profile2.AdmitByVerifyBudget(InclusionLists.InFillOrder(decoded.ByPosition), maxVerifyGasPerTx, signaturesValid,
            tx => InclusionListValidator.Profile2StaticChecks.IsWellFormed(tx, spec));
        IReadOnlySet<Hash256AsKey>? candidates = admitted.Count > 0 ? admitted : null;
        decoded.Profile2Candidates = (maxVerifyGasPerTx, candidates);
        return candidates;
    }

    private bool TryGetDecoded(PayloadAttributes? payloadAttributes, [NotNullWhen(true)] out DecodedInclusionLists? decoded)
    {
        decoded = null;
        if (payloadAttributes?.InclusionLists is not { } inclusionLists || IsEmpty(inclusionLists)) return false;
        if (!_decodedByAttributes.TryGetValue(inclusionLists, out Lazy<DecodedInclusionLists>? lazy))
        {
            // A miss means Set was never called for these attributes; debug-level as this runs once per improvement.
            if (_logger.IsDebug) _logger.Debug("No inclusion lists for this build — building without them.");
            return false;
        }

        try
        {
            decoded = lazy.Value;
            return true;
        }
        catch (Exception ex) when (ex is RlpException or ArgumentException)
        {
            // Lazy caches the failure, so malformed lists are reported once per build, not per improvement.
            if (_logger.IsWarn) _logger.Warn($"Discarding malformed inclusion lists ({ex.GetType().Name}: {ex.Message}); building without them.");
            return false;
        }
    }

    private static bool IsEmpty(byte[][][] inclusionLists)
    {
        foreach (byte[][]? inclusionList in inclusionLists)
            if (inclusionList is { Length: > 0 }) return false;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Decoding and sender recovery are deferred to the first <see cref="GetTransactions"/> call, so
    /// a forkchoice update that never starts a build pays nothing.</remarks>
    public void Set(byte[][][] inclusionLists, IReleaseSpec spec)
        => _decodedByAttributes.AddOrUpdate(inclusionLists,
            new Lazy<DecodedInclusionLists>(() =>
            {
                Transaction[][] byPosition = _decoder.Value.DecodeAndRecover(inclusionLists, spec);
                return new DecodedInclusionLists(OrderForProduction(FilterBlobs(InclusionLists.Distinct(byPosition))), byPosition, spec);
            }));

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
