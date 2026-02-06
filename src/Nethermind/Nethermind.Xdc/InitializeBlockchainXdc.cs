// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class InitializeBlockchainXdc(INethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider)
    : InitializeBlockchain(api, chainHeadInfoProvider)
{
    private readonly INethermindApi _api = api;
    protected override ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider)
    {
        _api.TxGossipPolicy.Policies.Add(new XdcTxGossipPolicy(_api.SpecProvider));

        TxPool.TxPool txPool = new(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                chainHeadInfoProvider,
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _api.TxGossipPolicy,
                new SignTransactionFilter(_api.EngineSigner, _api.BlockTree, _api.SpecProvider),
                _api.HeadTxValidator,
                true
            );

        _api.DisposeStack.Push(txPool);
        return txPool;
    }

    protected new IComparer<Transaction> CreateTxPoolTxComparer()
    {
        return new XdcTransactionComparerProvider(_api.SpecProvider!, _api.BlockTree!).GetDefaultComparer();
    }
}
