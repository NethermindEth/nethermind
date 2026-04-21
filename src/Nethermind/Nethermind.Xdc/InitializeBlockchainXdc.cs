// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.TxPool;
using Nethermind.Xdc.TxPool;
using System.Collections.Generic;

namespace Nethermind.Xdc;

internal class InitializeBlockchainXdc(INethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider, ITxGossipPolicy txGossipPolicy)
    : InitializeBlockchain(api, chainHeadInfoProvider, txGossipPolicy)
{
    private readonly INethermindApi _api = api;
    protected override ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider)
    {
        ISnapshotManager snapshotManager = _api.Context.Resolve<ISnapshotManager>();

        Nethermind.TxPool.TxPool txPool = new(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                chainHeadInfoProvider,
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _txGossipPolicy,
                new SignTransactionFilter(snapshotManager, _api.BlockTree, _api.SpecProvider),
                _api.HeadTxValidator,
                true
            );

        _api.DisposeStack.Push(txPool);
        return txPool;
    }

    protected new IComparer<Transaction> CreateTxPoolTxComparer() => new XdcTransactionComparerProvider(_api.SpecProvider!, _api.BlockTree!).GetDefaultComparer();
}
