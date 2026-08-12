// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    ILogManager? logManager) : ITxSource
{
    // Lazy<T> defaults to ExecutionAndPublication — once-only construction even under racing FCUs.
    private readonly Lazy<InclusionListDecoder> _decoder = new(() => new InclusionListDecoder(ecdsa, specProvider, logManager));
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<InclusionListTxSource>();

    // EIP-7805 (FOCIL): scope the decoded IL to its build, keyed by the build's PayloadAttributes
    // array, so a concurrent FCU can't leak another build's IL. Weak keys collect with the build.
    private readonly ConditionalWeakTable<byte[][], Transaction[]> _decodedByAttributes = [];

    // gasLimit is ignored — the downstream producer-side tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
    {
        if (payloadAttributes?.InclusionListTransactions is not { Length: > 0 } il) return [];
        if (_decodedByAttributes.TryGetValue(il, out Transaction[]? txs)) return txs;

        // A miss for a non-empty IL means Set never completed for this attrs instance — e.g. a malformed
        // or oversized IL discarded in engine_forkchoiceUpdatedV5, which already warned once with the cause.
        // Debug-level here since GetTransactions runs once per improvement iteration for the whole slot.
        if (_logger.IsDebug) _logger.Debug($"No decoded inclusion list for this build ({il.Length} entries) — building without it.");
        return [];
    }

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => _decodedByAttributes.AddOrUpdate(inclusionListTransactions, OrderForProduction(FilterBlobs(_decoder.Value.DecodeAndRecover(inclusionListTransactions, spec))));

    // The producer offers each IL tx to the block executor only once, so a lower nonce that appears
    // later than its dependent higher nonce (the IL is shuffled) would be skipped forever. Give each
    // sender a first-appearance index, then sort the array in place by (sender first-appearance, nonce):
    // each sender's txs ascend by nonce without imposing a cross-sender bias (a plain (sender, nonce)
    // sort would systematically favour low-address senders when the IL doesn't all fit).
    private static Transaction[] OrderForProduction(Transaction[] txs)
    {
        if (txs.Length < 2) return txs;

        // Unrecoverable senders (null) can never be included; group them together under Zero.
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

    // FOCIL: blob (type-3) IL entries are ignored — drop them so block production never emits a blob
    // tx that has no ShardBlobNetworkWrapper (which would make getPayloadV6 unusable for the CL).
    private static Transaction[] FilterBlobs(Transaction[] txs)
    {
        int kept = 0;
        for (int i = 0; i < txs.Length; i++)
            if (!txs[i].SupportsBlobs) kept++;
        if (kept == txs.Length) return txs;

        Transaction[] result = new Transaction[kept];
        int j = 0;
        for (int i = 0; i < txs.Length; i++)
            if (!txs[i].SupportsBlobs) result[j++] = txs[i];
        return result;
    }

    public bool SupportsBlobs => false;
}
