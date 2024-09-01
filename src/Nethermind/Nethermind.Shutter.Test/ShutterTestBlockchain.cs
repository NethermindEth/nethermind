// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

namespace Nethermind.Shutter.Test;

public class ShutterTestBlockchain(Random rnd, ITimestamper timestamper) : MergeAuRaTestBlockchain(null, null)
{
    public ShutterApiSimulator? Api;
    private readonly Random _rnd = rnd;
    private readonly ITimestamper _timestamper = timestamper;

    protected virtual ShutterApiSimulator CreateShutterApi(Random rnd, ITimestamper timestamper)
        => ShutterTestsCommon.InitApi(rnd, this, timestamper);

    protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
    {
        Api = CreateShutterApi(_rnd, _timestamper);
        _additionalTxSource = Api.TxSource;
        return base.CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
    }

    protected override IBlockImprovementContextFactory CreateBlockImprovementContextFactory(IBlockProducer blockProducer)
        => Api!.GetBlockImprovementContextFactory(blockProducer);
}
