// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    public bool ShouldGossipTransaction(Transaction tx) =>
        // EIP-8141: a blob-carrying frame tx has no EIP-7594 sidecar wrapper yet, so withhold it from
        // gossip rather than leak it in bare consensus form.
        tx.SupportsBlobs
            ? tx.GetProofVersion() == ChainHeadInfoProvider.CurrentProofVersion
            : !tx.CarriesBlobs;
}
