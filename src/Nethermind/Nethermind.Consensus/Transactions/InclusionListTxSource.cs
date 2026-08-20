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
    IEthereumEcdsa ecdsa,
    ISpecProvider specProvider,
    ILogManager logManager) : IInclusionListTxSource
{
    // Lazy<T> defaults to ExecutionAndPublication: constructed once even under racing FCUs.
    private readonly Lazy<InclusionListDecoder> _decoder = new(() => new InclusionListDecoder(ecdsa, specProvider, logManager));
    private readonly ILogger _logger = logManager.GetClassLogger<InclusionListTxSource>();

    // Keyed by the build's PayloadAttributes array so a concurrent FCU can't leak another build's IL;
    // weak keys collect with the build.
    private readonly ConditionalWeakTable<byte[][], Transaction[]> _decodedByAttributes = [];

    // gasLimit is ignored: the downstream tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
    {
        if (payloadAttributes?.InclusionListTransactions is not { Length: > 0 } il) return [];
        if (_decodedByAttributes.TryGetValue(il, out Transaction[]? txs)) return txs;

        // A miss means Set never completed for these attributes, e.g. a malformed or oversized IL that
        // engine_forkchoiceUpdatedV5 already warned about; debug-level as this runs once per improvement.
        if (_logger.IsDebug) _logger.Debug($"No decoded inclusion list for this build ({il.Length} entries) — building without it.");
        return [];
    }

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => _decodedByAttributes.AddOrUpdate(inclusionListTransactions, OrderForProduction(FilterBlobs(_decoder.Value.DecodeAndRecover(inclusionListTransactions, spec))));

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
