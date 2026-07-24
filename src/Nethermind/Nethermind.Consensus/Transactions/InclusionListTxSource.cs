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

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager) : ITxSource
{
    // Lazy<T> defaults to ExecutionAndPublication — once-only construction even under racing FCUs.
    private readonly Lazy<InclusionListDecoder> _decoder = new(() => new InclusionListDecoder(ecdsa, specProvider, logManager));

    // EIP-7805 (FOCIL): scope the decoded IL to its build, keyed by the build's PayloadAttributes
    // array, so a concurrent FCU can't leak another build's IL. Weak keys collect with the build.
    private readonly ConditionalWeakTable<byte[][], Transaction[]> _decodedByAttributes = [];

    // gasLimit is ignored — the downstream producer-side tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => payloadAttributes?.InclusionListTransactions is { } il && _decodedByAttributes.TryGetValue(il, out Transaction[]? txs)
            ? txs
            : [];

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => _decodedByAttributes.AddOrUpdate(inclusionListTransactions, OrderForProduction(FilterBlobs(_decoder.Value.DecodeAndRecover(inclusionListTransactions, spec))));

    // The producer offers each IL tx to the block executor only once, so a lower nonce that appears
    // later than its dependent higher nonce (the IL is shuffled) would be skipped forever. Ordering by
    // (sender, nonce) guarantees each sender's txs are offered in ascending-nonce (dependency) order.
    private static Transaction[] OrderForProduction(Transaction[] txs)
    {
        Array.Sort(txs, static (a, b) =>
        {
            int bySender = CompareSenders(a.SenderAddress, b.SenderAddress);
            return bySender != 0 ? bySender : a.Nonce.CompareTo(b.Nonce);
        });
        return txs;
    }

    private static int CompareSenders(Address? a, Address? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        // Unrecoverable senders (null) can never be included; order them last.
        if (a is null) return 1;
        if (b is null) return -1;
        return a.Bytes.SequenceCompareTo(b.Bytes);
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
