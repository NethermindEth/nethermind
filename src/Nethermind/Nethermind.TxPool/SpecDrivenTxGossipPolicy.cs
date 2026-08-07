// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    public bool ShouldGossipTransaction(Transaction tx) =>
        // EIP8141: a blob-carrying frame tx (type 6) has no EIP-7594 sidecar network wrapper yet, so it
        // cannot participate in the announce-by-hash / serve-with-sidecar blob gossip protocol. Withhold
        // it from gossip until that wire format lands, rather than leaking it in bare consensus form.
        tx.SupportsBlobs
            ? tx.GetProofVersion() == ChainHeadInfoProvider.CurrentProofVersion
            : !tx.CarriesBlobs;
}
