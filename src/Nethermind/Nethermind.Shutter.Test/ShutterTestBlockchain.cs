// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

namespace Nethermind.Shutter.Test;

public class ShutterTestBlockchain(Random rnd, ITimestamper? timestamper = null, ShutterEventSimulator? eventSimulator = null) : MergeAuRaTestBlockchain(null, null)
{
    public ShutterApiSimulator? Api { get => _api; }
    private ShutterApiSimulator? _api;
    protected readonly Random _rnd = rnd;
    protected readonly ITimestamper? _timestamper = timestamper;

    protected virtual ShutterApiSimulator CreateShutterApi()
        => ShutterTestsCommon.InitApi(_rnd, this, _timestamper, eventSimulator);

    protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
    {
        _api = CreateShutterApi();
        _additionalTxSource = _api.TxSource;
        return base.CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
    }

    protected override IBlockImprovementContextFactory CreateBlockImprovementContextFactory(IBlockProducer blockProducer)
        => _api!.GetBlockImprovementContextFactory(blockProducer);
}
