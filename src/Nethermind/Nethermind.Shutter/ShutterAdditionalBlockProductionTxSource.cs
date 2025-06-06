// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Shutter;

public class ShutterAdditionalBlockProductionTxSource(IBlockProducerTxSourceFactory baseFactory, ShutterApi shutterApi) : IBlockProducerTxSourceFactory
{
    public ITxSource Create()
    {
        return shutterApi.TxSource.Then(baseFactory.Create());
    }
}
