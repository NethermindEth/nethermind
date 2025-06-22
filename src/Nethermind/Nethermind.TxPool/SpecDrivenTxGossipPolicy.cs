// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class SpecDrivenTxGossipPolicy(IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    private IChainHeadInfoProvider ChainHeadInfoProvider { get; } = chainHeadInfoProvider;

    public bool ShouldGossipTransaction(Transaction tx) =>
        !tx.SupportsBlobs || (tx.NetworkWrapper as ShardBlobNetworkWrapper)?.Version == ChainHeadInfoProvider.CurrentProofVersion;
}
