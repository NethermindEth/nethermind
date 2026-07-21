// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(
    TaikoNethermindApi api,
    IChainHeadInfoProvider chainHeadInfoProvider,
    ITxGossipPolicy txGossipPolicy,
    TaikoBeaconHeadAdvancer headAdvancer) : InitializeBlockchain(api, chainHeadInfoProvider, txGossipPolicy)
{
    private readonly TaikoBeaconHeadAdvancer _headAdvancer = headAdvancer;

    /// <summary>
    /// Overrides the TxPool creation to disable blob (type-3) transaction support.
    /// Taiko does not use blobs on L2, so blob transactions must be rejected at the mempool boundary.
    /// </summary>
    protected override ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider)
    {
        api.Config<ITxPoolConfig>().BlobsSupport = BlobsSupportMode.Disabled;
        return base.CreateTxPool(chainHeadInfoProvider);
    }
}
