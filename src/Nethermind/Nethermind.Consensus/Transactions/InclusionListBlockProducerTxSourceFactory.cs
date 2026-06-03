// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Consensus.Transactions;

public class InclusionListBlockProducerTxSourceFactory(
    IBlockProducerTxSourceFactory baseFactory,
    InclusionListTxSource inclusionListTxSource) : IBlockProducerTxSourceFactory
{
    public ITxSource Create() => inclusionListTxSource.Then(baseFactory.Create());
}
