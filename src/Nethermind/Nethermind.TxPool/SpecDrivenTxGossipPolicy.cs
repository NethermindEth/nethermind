// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    /// <remarks>EIP-8437 dependency-bearing transactions are gossiped too; each peer connection carries them only from
    /// <c>eth/73</c>.</remarks>
    public bool ShouldGossipTransaction(Transaction tx) =>
        (!tx.SupportsBlobs && !tx.CarriesBlobs) || tx.GetProofVersion() == ChainHeadInfoProvider.CurrentProofVersion;
}
