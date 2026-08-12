// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    public bool ShouldGossipTransaction(Transaction tx) =>
        // EIP-8141: a blob-carrying frame tx (type 6) shares the EIP-7594 sidecar wrapper with type-3, so it
        // gossips on the same terms — only once its sidecar is at the current proof version.
        (!tx.SupportsBlobs && !tx.CarriesBlobs) || tx.GetProofVersion() == ChainHeadInfoProvider.CurrentProofVersion;
}
