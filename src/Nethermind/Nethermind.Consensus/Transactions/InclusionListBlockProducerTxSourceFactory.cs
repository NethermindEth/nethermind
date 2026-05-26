// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// Appends FOCIL (EIP-7805) inclusion-list transactions to the block-producer tx source.
/// The CL injects IL bytes via <see cref="InclusionListTxSource.Set"/> after engine_updatePayloadWithInclusionListV1;
/// they are then drained on the next block-production cycle.
/// </summary>
public class InclusionListBlockProducerTxSourceFactory(
    IBlockProducerTxSourceFactory baseFactory,
    InclusionListTxSource inclusionListTxSource) : IBlockProducerTxSourceFactory
{
    public ITxSource Create() => baseFactory.Create().Then(inclusionListTxSource);
}
