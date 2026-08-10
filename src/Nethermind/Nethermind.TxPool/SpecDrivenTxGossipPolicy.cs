// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    public bool ShouldGossipTransaction(Transaction tx) =>
        // EIP-8141: a blob-carrying frame tx (type 6) shares the EIP-7594 sidecar wrapper with type-3, so
        // it takes part in the announce-by-hash / serve-with-sidecar protocol on the same terms — gossiped
        // only with a current-proof-version sidecar, and withheld while it still lacks one.
        (!tx.SupportsBlobs && !tx.CarriesBlobs) || tx.GetProofVersion() == ChainHeadInfoProvider.CurrentProofVersion;
}
