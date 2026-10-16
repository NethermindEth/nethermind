// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    /// <remarks>EIP-8437: dependency-bearing transactions, those with a dependency verification frame, are never broadcast or
    /// announced through Transactions or NewPooledTransactionHashes; peers discover them through EIP-8288 proof wrappers.</remarks>
    public bool ShouldGossipTransaction(Transaction tx) =>
        !Eip8288Dependencies.IsDependencyBearing(tx)
        && ((!tx.SupportsBlobs && !tx.CarriesBlobs) || tx.GetProofVersion() == ChainHeadInfoProvider.CurrentProofVersion);
}
